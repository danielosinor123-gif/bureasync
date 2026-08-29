using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BureauSync.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
var builder=WebApplication.CreateBuilder(args);
var key=builder.Configuration["Jwt:Key"]??"";
if(key.Length<32)throw new InvalidOperationException("Set Jwt:Key through user secrets or environment variables; it must be 32+ characters.");
var connection=builder.Configuration.GetConnectionString("BureauSync")??throw new InvalidOperationException("BureauSync connection string missing.");
var provider=builder.Configuration["DatabaseProvider"]??"SqlServer";
builder.Services.AddDbContext<BureauSyncDb>(x=>{if(provider.Equals("Sqlite",StringComparison.OrdinalIgnoreCase))x.UseSqlite(connection);else x.UseSqlServer(connection);});
builder.Services.AddScoped<CsvValidator>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(x=>x.TokenValidationParameters=new TokenValidationParameters{ValidateIssuer=true,ValidIssuer=builder.Configuration["Jwt:Issuer"],ValidateAudience=true,ValidAudience=builder.Configuration["Jwt:Audience"],ValidateIssuerSigningKey=true,IssuerSigningKey=new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),ValidateLifetime=true,ClockSkew=TimeSpan.FromSeconds(30)});
builder.Services.AddAuthorization();
builder.Services.AddCors(o=>o.AddPolicy("frontend", p=>{
 var origins = builder.Configuration["FrontendUrl"]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
 if(origins==null||origins.Length==0) origins = new[]{"http://localhost:5000","http://localhost:3000","https://*.vercel.app"};
 p.SetIsOriginAllowedToAllowWildcardSubdomains().WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));
builder.Services.Configure<ForwardedHeadersOptions>(o=>{o.ForwardedHeaders=ForwardedHeaders.XForwardedFor|ForwardedHeaders.XForwardedProto; o.KnownNetworks.Clear(); o.KnownProxies.Clear();});
var app=builder.Build();
if(app.Environment.IsDevelopment()&&provider.Equals("Sqlite",StringComparison.OrdinalIgnoreCase)){using var scope=app.Services.CreateScope();scope.ServiceProvider.GetRequiredService<BureauSyncDb>().Database.EnsureCreated();}
app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseExceptionHandler(e=>e.Run(async c=>{c.Response.StatusCode=500;await c.Response.WriteAsJsonAsync(new{error="Unexpected server error.",traceId=c.TraceIdentifier});}));
if(!string.Equals(Environment.GetEnvironmentVariable("DISABLE_HTTPS_REDIRECT"), "1", StringComparison.OrdinalIgnoreCase))
{
    app.UseHttpsRedirection();
}
app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();
if(app.Environment.IsDevelopment()){app.UseSwagger();app.UseSwaggerUI();}
app.MapGet("/health",()=>Results.Ok(new{status="ok",service="BureauSync.Api"})).AllowAnonymous();
app.MapPost("/api/auth/register",async(RegisterRequest r,BureauSyncDb db)=>{if(await db.Users.AnyAsync())return Results.StatusCode(StatusCodes.Status403Forbidden);if(r.Password.Length<14)return Results.BadRequest(new{error="Password must be at least 14 characters."});if(r.Role!=Roles.Admin)return Results.BadRequest(new{error="The one-time bootstrap account must be a BureauAdmin."});var email=r.Email.Trim().ToLowerInvariant();db.Users.Add(new User{Email=email,PasswordHash=HashPassword(r.Password),Role=Roles.Admin});await db.SaveChangesAsync();return Results.Created("/api/auth/login",new{message="Initial BureauAdmin account created. Use the authenticated user endpoint to provision additional users."});}).AllowAnonymous();
app.MapPost("/api/auth/login",async(LoginRequest r,BureauSyncDb db)=>{var u=await db.Users.SingleOrDefaultAsync(x=>x.Email==r.Email.Trim().ToLowerInvariant());if(u is null||!u.IsActive||!VerifyPassword(r.Password,u.PasswordHash))return Results.Unauthorized();return Results.Ok(IssueToken(u,builder.Configuration,key));}).AllowAnonymous();
var api=app.MapGroup("/api").RequireAuthorization();
api.MapPost("/users",async(RegisterRequest r,BureauSyncDb db,ClaimsPrincipal p)=>{if(!p.IsInRole(Roles.Admin))return Results.Forbid();if(r.Password.Length<14)return Results.BadRequest(new{error="Password must be at least 14 characters."});if(!new[]{Roles.Admin,Roles.Operator,Roles.Submitter}.Contains(r.Role))return Results.BadRequest(new{error="Invalid role."});var email=r.Email.Trim().ToLowerInvariant();if(await db.Users.AnyAsync(x=>x.Email==email))return Results.Conflict(new{error="Email already exists."});var user=new User{Email=email,PasswordHash=HashPassword(r.Password),Role=r.Role};db.Users.Add(user);Audit(db,p,"User.Provisioned","User",user.Id.ToString(),user.Role);await db.SaveChangesAsync();return Results.Created("/api/users/"+user.Id,new{user.Id,user.Email,user.Role});}).RequireAuthorization(x=>x.RequireRole(Roles.Admin));
api.MapPost("/lenders",async(LenderRequest r,BureauSyncDb db,ClaimsPrincipal p,HttpContext ctx)=>{
if(!p.IsInRole(Roles.Admin))return Results.Forbid();
var code=r.Code.Trim().ToUpperInvariant();
if(await db.Lenders.AnyAsync(x=>x.Code==code))return Results.Conflict(new{error="Lender code already exists."});
if(!string.IsNullOrWhiteSpace(r.SwiftBic)&&await db.Lenders.AnyAsync(x=>x.SwiftBic!=null&&x.SwiftBic.ToUpper()==r.SwiftBic.Trim().ToUpperInvariant()))return Results.Conflict(new{error="SWIFT/BIC already registered."});
if(!string.IsNullOrWhiteSpace(r.CbnLicense)&&await db.Lenders.AnyAsync(x=>x.CbnLicense!=null&&x.CbnLicense.ToUpper()==r.CbnLicense.Trim().ToUpperInvariant()))return Results.Conflict(new{error="CBN License already registered."});
if(!string.IsNullOrWhiteSpace(r.Lei)&&await db.Lenders.AnyAsync(x=>x.Lei!=null&&x.Lei.ToUpper()==r.Lei.Trim().ToUpperInvariant()))return Results.Conflict(new{error="LEI already registered."});
if(!string.IsNullOrWhiteSpace(r.CustomId)&&await db.Lenders.AnyAsync(x=>x.CustomId!=null&&x.CustomId.ToUpper()==r.CustomId.Trim().ToUpperInvariant()))return Results.Conflict(new{error="Custom ID already registered."});
var lender=new Lender{Code=code,Name=r.Name.Trim(),SwiftBic=string.IsNullOrWhiteSpace(r.SwiftBic)?null:r.SwiftBic.Trim(),CbnLicense=string.IsNullOrWhiteSpace(r.CbnLicense)?null:r.CbnLicense.Trim(),Lei=string.IsNullOrWhiteSpace(r.Lei)?null:r.Lei.Trim(),CustomId=string.IsNullOrWhiteSpace(r.CustomId)?null:r.CustomId.Trim()};
db.Lenders.Add(lender);
Audit(db,p,"Lender.Created","Lender",lender.Id.ToString(),code);
await db.SaveChangesAsync();
return Results.Created("/api/lenders/"+lender.Id,lender);
});
api.MapGet("/lenders",async(BureauSyncDb db)=>Results.Ok(await db.Lenders.OrderBy(x=>x.Code).Select(x=>new{x.Id,x.Code,x.Name,x.SwiftBic,x.CbnLicense,x.Lei,x.CustomId,x.IsActive}).ToListAsync()));
api.MapPost("/lenders/{id:guid}/submissions",async(Guid id,IFormFile file,BureauSyncDb db,CsvValidator validator,ClaimsPrincipal p,IConfiguration cfg)=>{if(!(p.IsInRole(Roles.Admin)||p.IsInRole(Roles.Operator)||p.IsInRole(Roles.Submitter)))return Results.Forbid();var max=cfg.GetValue<long>("Safety:MaxUploadBytes");if(file is null||file.Length==0||file.Length>max||!file.FileName.EndsWith(".csv",StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new{error="Provide a non-empty .csv within the configured upload limit."});var lender=await db.Lenders.FindAsync(id);if(lender is null||!lender.IsActive)return Results.NotFound();List<SubmissionRecord> records;string hash;try{await using var stream=file.OpenReadStream();(records,hash)=validator.Validate(stream,lender);}catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}if(await db.Submissions.AnyAsync(x=>x.LenderId==id&&x.FileHash==hash))return Results.Conflict(new{error="This exact file was already submitted."});var sub=new Submission{LenderId=id,FileName=Path.GetFileName(file.FileName),FileHash=hash,Records=records,Total=records.Count,Ready=records.Count(x=>x.Outcome=="Ready"),Review=records.Count(x=>x.Outcome=="Review"),Rejected=records.Count(x=>x.Outcome=="Rejected")};db.Submissions.Add(sub);Audit(db,p,"Submission.Validated","Submission",sub.Id.ToString(),sub.Total+" records; "+sub.Rejected+" rejected");await db.SaveChangesAsync();return Results.Created("/api/submissions/"+sub.Id,Summary(sub,lender));}).DisableAntiforgery();
api.MapGet("/submissions",async(BureauSyncDb db)=>Results.Ok((await db.Submissions.Join(db.Lenders,s=>s.LenderId,l=>l.Id,(s,l)=>new SubmissionSummary(s.Id,l.Code,s.FileName,s.State,s.Total,s.Ready,s.Review,s.Rejected,s.SubmittedAt)).ToListAsync()).OrderByDescending(x=>x.SubmittedAt).ToList()));
api.MapGet("/submissions/{id:guid}/records",async(Guid id,BureauSyncDb db)=>Results.Ok(await db.SubmissionRecords.Where(x=>x.SubmissionId==id).Include(x=>x.Issues).OrderBy(x=>x.RowNumber).Select(x=>new {x.Id,x.RowNumber,x.AccountNumber,x.Outcome,Issues=x.Issues.Select(i=>new{i.RuleCode,i.Field,i.Severity,i.Message,i.SuggestedAction})}).ToListAsync()));
api.MapPatch("/submissions/{id:guid}/state",async(Guid id,StateRequest r,BureauSyncDb db,ClaimsPrincipal p)=>{if(!(p.IsInRole(Roles.Admin)||p.IsInRole(Roles.Operator)))return Results.Forbid();if(r.State=="Ingested")return Results.BadRequest(new{error="Ingestion is intentionally outside this baseline API."});var s=await db.Submissions.FindAsync(id);if(s is null)return Results.NotFound();s.State=r.State;Audit(db,p,"Submission.StateChanged","Submission",id.ToString(),r.State);await db.SaveChangesAsync();return Results.Ok(new{s.Id,s.State});});
app.Run();
static string HashPassword(string password){var salt=RandomNumberGenerator.GetBytes(16);var hash=Rfc2898DeriveBytes.Pbkdf2(password,salt,310000,HashAlgorithmName.SHA256,32);return Convert.ToBase64String(salt)+":"+Convert.ToBase64String(hash);}static bool VerifyPassword(string p,string saved){var parts=saved.Split(':');if(parts.Length!=2)return false;var actual=Rfc2898DeriveBytes.Pbkdf2(p,Convert.FromBase64String(parts[0]),310000,HashAlgorithmName.SHA256,32);return CryptographicOperations.FixedTimeEquals(actual,Convert.FromBase64String(parts[1]));}static object IssueToken(User u,IConfiguration c,string key){var exp=DateTime.UtcNow.AddMinutes(c.GetValue<int>("Jwt:AccessTokenMinutes"));var claims=new[]{new Claim(JwtRegisteredClaimNames.Sub,u.Id.ToString()),new Claim(ClaimTypes.NameIdentifier,u.Id.ToString()),new Claim(ClaimTypes.Email,u.Email),new Claim(ClaimTypes.Role,u.Role)};var jwt=new JwtSecurityToken(c["Jwt:Issuer"],c["Jwt:Audience"],claims,expires:exp,signingCredentials:new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),SecurityAlgorithms.HmacSha256));return new{accessToken=new JwtSecurityTokenHandler().WriteToken(jwt),expiresAt=exp,role=u.Role};}static void Audit(BureauSyncDb db,ClaimsPrincipal p,string action,string type,string id,string detail){var actor=Guid.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier),out var value)?value:(Guid?)null;db.AuditEvents.Add(new AuditEvent{ActorId=actor,Action=action,EntityType=type,EntityId=id,Detail=detail});}static object Summary(Submission s,Lender l)=>new SubmissionSummary(s.Id,l.Code,s.FileName,s.State,s.Total,s.Ready,s.Review,s.Rejected,s.SubmittedAt);
public record RegisterRequest(string Email,string Password,string Role);public record LoginRequest(string Email,string Password);public record LenderRequest(string Code,string Name,string? SwiftBic,string? CbnLicense,string? Lei,string? CustomId);public record StateRequest(string State);public record SubmissionSummary(Guid Id,string LenderCode,string FileName,string State,int Total,int Ready,int Review,int Rejected,DateTimeOffset SubmittedAt);
