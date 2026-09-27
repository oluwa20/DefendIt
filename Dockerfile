FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/DefendIt/DefendIt.csproj src/DefendIt/
RUN dotnet restore src/DefendIt/DefendIt.csproj
COPY src/ src/
RUN dotnet publish src/DefendIt/DefendIt.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production
# Render (and most PaaS) inject PORT; default to 8080 locally.
CMD ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} dotnet DefendIt.dll"]
