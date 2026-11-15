FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY Directory.Packages.props ./
COPY src/EventIngestion.Api/EventIngestion.Api.csproj src/EventIngestion.Api/
COPY src/EventIngestion.Application/EventIngestion.Application.csproj src/EventIngestion.Application/
COPY src/EventIngestion.Domain/EventIngestion.Domain.csproj src/EventIngestion.Domain/
COPY src/EventIngestion.Infrastructure/EventIngestion.Infrastructure.csproj src/EventIngestion.Infrastructure/
RUN dotnet restore src/EventIngestion.Api/EventIngestion.Api.csproj

COPY src/ src/
RUN dotnet publish src/EventIngestion.Api/EventIngestion.Api.csproj --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "EventIngestion.Api.dll"]
