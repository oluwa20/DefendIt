FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ src/
# Single-step publish: a separate restore + "publish --no-restore" drops the Blazor
# framework scripts (_framework/blazor.web.js) from the output in .NET 10.
RUN dotnet publish src/DefendIt/DefendIt.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production
# Render (and most PaaS) inject PORT; default to 8080 locally.
CMD ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} dotnet DefendIt.dll"]
