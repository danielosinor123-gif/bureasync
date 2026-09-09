#pragma warning disable CS0162,CS1998,CS9113
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BureauSync.Api;

// ---- Ollama client (Qwen2.5-3B Q4_K_M via localhost:11434) ----
public class OllamaClient(HttpClient http, IConfiguration cfg, ILogger<OllamaClient> log)
{
    private string Endpoint => cfg["Ollama:Endpoint"] ?? "http://localhost:11434";
    private string Model => cfg["Ollama:Model"] ?? "qwen2.5:3b-instruct-q4_K_M";
    // generous timeout for CPU 5-10 tok/sec
    public async Task<string> GenerateAsync(string prompt, string? format = null, CancellationToken ct = default)
    {
        var url = $"{Endpoint.TrimEnd('/')}/api/generate";
        var payload = new { model = Model, prompt, stream = false, format, options = new { temperature = 0.2, num_predict = 512 } };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            var resp = await http.PostAsJsonAsync(url, payload, cts.Token);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
            if (body.TryGetProperty("response", out var r)) return r.GetString() ?? "";
            return body.ToString();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Ollama generate failed model={Model} endpoint={Endpoint}", Model, Endpoint);
            throw;
        }
    }
    public async Task<string> ChatAsync(string system, string user, string? format = null, CancellationToken ct = default)
    {
        var prompt = $"<|im_start|>system\n{system}<|im_end|>\n<|im_start|>user\n{user}<|im_end|>\n<|im_start|>assistant\n";
        return await GenerateAsync(prompt, format, ct);
    }
}

// ---- Background queue (jobs 2-4 never block HTTP) ----
public record LlmWorkItem(LlmJobType JobType, Guid EntityId, Func<CancellationToken, Task> Execute);

public class LlmBackgroundQueue
{
    private readonly ConcurrentQueue<LlmWorkItem> _q = new();
    private readonly SemaphoreSlim _sig = new(0);
    public void Enqueue(LlmWorkItem item) { _q.Enqueue(item); _sig.Release(); }
    public async Task<LlmWorkItem> DequeueAsync(CancellationToken ct)
    {
        await _sig.WaitAsync(ct);
        _q.TryDequeue(out var item);
        return item!;
    }
    public int Count => _q.Count;
}

public class LlmBackgroundWorker(LlmBackgroundQueue queue, ILogger<LlmBackgroundWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("LlmBackgroundWorker started");
        while (!ct.IsCancellationRequested)
        {
            LlmWorkItem item;
            try { item = await queue.DequeueAsync(ct); }
            catch (OperationCanceledException) { break; }
            try
            {
                log.LogInformation("Processing {Job} {Entity}", item.JobType, item.EntityId);
                await item.Execute(ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Background job failed {Job} {Entity}", item.JobType, item.EntityId);
                // do not retry eagerly on CPU timeout; leave for operator / next poll
            }
            // be tolerant of slow CPU: small pause between jobs
            await Task.Delay(500, ct);
        }
    }
}

// ---- Job implementations ----
// Job 4: Operator explanation (lowest stakes, validates Ollama)
public class OperatorExplanationService(OllamaClient ollama, IServiceScopeFactory scopeFactory, LlmBackgroundQueue queue, ILogger<OperatorExplanationService> log)
{
    public void Enqueue(Guid recordId)
    {
        queue.Enqueue(new LlmWorkItem(LlmJobType.OperatorExplanation, recordId, async ct =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BureauSyncDb>();
            var r = await db.SubmissionRecords.Include(x=>x.Issues).SingleOrDefaultAsync(x=>x.Id==recordId, ct);
            var prompt = BuildPrompt(r);
            string output;
            try { output = await ollama.GenerateAsync(prompt, ct: ct); }
            catch { output = "Explanation unavailable — Ollama not reachable. Raw fields: " + (r?.AccountNumber ?? recordId.ToString()); }
            var ent = new OperatorExplanation { SubmissionRecordId = recordId, GeneratedText = output.Trim() };
            db.OperatorExplanations.Add(ent);
            await db.SaveChangesAsync(ct);
            log.LogInformation("Operator explanation created for {Record}", recordId);
        }));
    }
    private static string BuildPrompt(SubmissionRecord? r)
    {
        if (r == null) return "Summarize this exception in one paragraph for a bureau operator.";
        return $"You are a bureau operator assistant. Write one paragraph plain English summary for the review queue. Do not invent identifiers.\nRow {r.RowNumber} account {r.AccountNumber} outcome {r.Outcome} status {r.LoanStatus} loan {r.LoanAmount} balance {r.OutstandingBalance} issues {r.Issues.Count}.";
    }
}

// Job 2: Root-cause classification — fixed-order structured prompt
public class RootCauseClassificationService(OllamaClient ollama, IServiceScopeFactory scopeFactory, LlmBackgroundQueue queue, ILogger<RootCauseClassificationService> log)
{
    private static readonly string[] Categories = Enum.GetNames(typeof(RootCauseCategory));
    public void Enqueue(Guid recordId)
    {
        queue.Enqueue(new LlmWorkItem(LlmJobType.RootCauseClassification, recordId, async ct =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BureauSyncDb>();
            var rec = await db.SubmissionRecords.Include(x => x.Issues).SingleOrDefaultAsync(x => x.Id == recordId, ct);
            if (rec == null) return;
            var snapshot = JsonSerializer.Serialize(new { ledger = new { rec.LenderCode, rec.AccountNumber, rec.LoanAmount, rec.OutstandingBalance, rec.LoanStatus }, issues = rec.Issues.Select(i => new { i.RuleCode, i.Field, i.Message }), row = rec.RowNumber });
            // Fixed-order prompt: ledger fields, bureau fields, payment summary — do not change order
            var user = $"LEDGER_FIELDS:\nLenderCode={rec.LenderCode}\nAccount={rec.AccountNumber}\nLoanAmount={rec.LoanAmount}\nOutstanding={rec.OutstandingBalance}\nStatus={rec.LoanStatus}\nRow={rec.RowNumber}\nISSUES:\n{string.Join(";", rec.Issues.Select(i => i.RuleCode + ":" + i.Message))}\n\nClassify into one of: {string.Join(", ", Categories)}. Return JSON {{\"category\":\"...\", \"confidence\":0.0-1.0}} only.";
            var system = "You are a deterministic classifier. Return only JSON with category and confidence. No prose.";
            string raw;
            try { raw = await ollama.ChatAsync(system, user, format: "json", ct); }
            catch (Exception ex) { log.LogWarning(ex, "Ollama classification failed {Record}", recordId); raw = "{\"category\":\"PaymentNotPosted\",\"confidence\":0.5}"; }
            RootCauseCategory cat = RootCauseCategory.PaymentNotPosted;
            double conf = 0.5;
            try
            {
                var doc = JsonDocument.Parse(raw);
                var c = doc.RootElement.GetProperty("category").GetString() ?? "";
                if (Enum.TryParse<RootCauseCategory>(c, true, out var parsed)) cat = parsed;
                if (doc.RootElement.TryGetProperty("confidence", out var cf) && cf.TryGetDouble(out var v)) conf = v;
            }
            catch { /* fallback */ }
            var ent = new RootCauseClassification { SubmissionRecordId = recordId, Category = cat, Confidence = conf, ModelInputSnapshot = snapshot, Status = RootCauseStatus.Pending };
            db.RootCauseClassifications.Add(ent);
            await db.SaveChangesAsync(ct);
        }));
    }
}

// Job 1: Ingestion mapping (synchronous with upload but cache-first)
public class LlmColumnMapper(OllamaClient ollama, BureauSyncDb db, ILogger<LlmColumnMapper> log)
{
    private static readonly string[] Canonical = new[] { "lender_id","borrower_name","identity_id","loan_account_no","loan_amount","outstanding_balance","status","disbursement_date","due_date","repayment_date" };
    private static readonly string CanonicalDesc = string.Join(", ", Canonical) + " — BureauSync canonical 10-column schema";
    public async Task<Dictionary<string,string>> MapHeadersAsync(Guid lenderId, string[] sourceHeaders, Dictionary<string,string[]> sampleValues, double threshold = 0.75)
    {
        var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var known = await db.LenderColumnMappings.Where(x => x.LenderId == lenderId && x.ApprovedByOperator).ToListAsync();
        var knownMap = known.ToDictionary(k => k.SourceColumnName, k => k.CanonicalField, StringComparer.OrdinalIgnoreCase);
        foreach (var h in sourceHeaders)
        {
            if (knownMap.TryGetValue(h.Trim(), out var canon)) { result[h] = canon; continue; }
            // check if header already canonical (case-insensitive)
            var lower = h.Trim().ToLowerInvariant();
            if (Canonical.Contains(lower)) { result[h] = lower; continue; }
            // LLM call for unmapped
            var samples = sampleValues.TryGetValue(h, out var vals) ? string.Join(", ", vals.Take(5)) : "";
            var prompt = $"Map this lender column header to the canonical schema.\nUnmapped header: \"{h}\"\nCanonical fields: {CanonicalDesc}\nExample values for \"{h}\": {samples}\nReturn JSON {{\"canonical\":\"...\",\"confidence\":0.0-1.0}} where canonical is one of the 10 names or \"UNKNOWN\".";
            string raw;
            try { raw = await ollama.GenerateAsync(prompt, format: "json"); }
            catch (Exception ex) { log.LogWarning(ex, "LlmColumnMapper Ollama failed for {Header}", h); raw = "{\"canonical\":\"UNKNOWN\",\"confidence\":0.0}"; }
            string suggested = "UNKNOWN"; double conf = 0;
            try { var d = JsonDocument.Parse(raw); suggested = d.RootElement.GetProperty("canonical").GetString() ?? "UNKNOWN"; if(d.RootElement.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var v)) conf = v; } catch {}
            suggested = suggested.ToLowerInvariant();
            if (conf < threshold || suggested == "unknown" || !Canonical.Contains(suggested))
            {
                // cold-start handling: queue for human, do not guess silently
                var q = new MappingReviewQueue { LenderId = lenderId, SourceColumnName = h, SuggestedCanonicalField = suggested, Confidence = conf, SampleValuesJson = JsonSerializer.Serialize(sampleValues.TryGetValue(h, out var s) ? s.Take(5).ToArray() : Array.Empty<string>()), Status = "Pending" };
                db.MappingReviewQueues.Add(q);
                await db.SaveChangesAsync();
                // do not add to result — caller must treat as unresolved
                continue;
            }
            // high confidence: store as not yet approved (will be approved when operator confirms)
            var mapping = new LenderColumnMapping { LenderId = lenderId, SourceColumnName = h, CanonicalField = suggested, Confidence = conf, ApprovedByOperator = false };
            db.LenderColumnMappings.Add(mapping);
            await db.SaveChangesAsync();
            result[h] = suggested;
        }
        return result;
    }
}

// Job 3: Correction drafting — template + constrained narrative only
public class CorrectionDraftService(OllamaClient ollama, IServiceScopeFactory scopeFactory, LlmBackgroundQueue queue)
{
    private static readonly Dictionary<string,string> Templates = new()
    {
        ["Default"] = "BUREAU CORRECTION — {{BureauFormat}}\nAccount: {{AccountNumber}}\nLender: {{LenderCode}}\nAmount: {{LoanAmount}} Balance: {{OutstandingBalance}} Status: {{LoanStatus}}\nRoot cause: {{Category}}\nEvidence: {{Narrative}}\n— Interpolated from DB, not generated."
    };
    public void Enqueue(Guid recordId, Guid classificationId, string bureauFormat)
    {
        queue.Enqueue(new LlmWorkItem(LlmJobType.CorrectionDraft, recordId, async ct =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BureauSyncDb>();
            var rec = await db.SubmissionRecords.FindAsync(new object[] { recordId }, ct);
            var cls = await db.RootCauseClassifications.FindAsync(new object[] { classificationId }, ct);
            if (rec == null || cls == null) return;
            var prompt = $"Write 1-2 sentences of evidence narrative for a bureau correction. Bureau={bureauFormat} Category={cls.Category}. Do not include account numbers, amounts, or dates — they will be templated.";
            string narrative;
            try { narrative = await ollama.GenerateAsync(prompt, ct: ct); }
            catch { narrative = "Evidence reviewed; correction requested per bureau template."; }
            narrative = narrative.Trim();
            if (narrative.Length > 500) narrative = narrative[..500];
            var doc = Render(bureauFormat, rec, cls, narrative);
            var draft = new CorrectionDraft { SubmissionRecordId = recordId, RootCauseClassificationId = classificationId, BureauFormat = bureauFormat, GeneratedNarrative = narrative, RenderedDocument = doc, OperatorStatus = CorrectionOperatorStatus.Draft };
            db.CorrectionDrafts.Add(draft);
            await db.SaveChangesAsync(ct);
        }));
    }
    public static string Render(string bureauFormat, SubmissionRecord rec, RootCauseClassification cls, string narrative)
    {
        var tpl = Templates.TryGetValue(bureauFormat, out var t) ? t : Templates["Default"];
        return tpl.Replace("{{BureauFormat}}", bureauFormat).Replace("{{AccountNumber}}", rec.AccountNumber).Replace("{{LenderCode}}", rec.LenderCode).Replace("{{LoanAmount}}", rec.LoanAmount?.ToString() ?? "").Replace("{{OutstandingBalance}}", rec.OutstandingBalance?.ToString() ?? "").Replace("{{LoanStatus}}", rec.LoanStatus).Replace("{{Category}}", cls.Category.ToString()).Replace("{{Narrative}}", narrative);
    }
}
