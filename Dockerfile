# BureauSync API - .NET 8
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY BureauSync.Api.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish ./
# Vercel/Render inject PORT; Kestrel listens on 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
# SQLite file will be ephemeral on serverless - use external DB for prod via ConnectionStrings__BureauSync
# For demo, SQLite is created via EnsureCreated in Development; in Production mount a volume or set DatabaseProvider=SqlServer
ENTRYPOINT ["dotnet", "BureauSync.Api.dll"]
