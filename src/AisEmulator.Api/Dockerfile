FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/AisEmulator.Api/AisEmulator.Api.csproj src/AisEmulator.Api/
RUN dotnet restore src/AisEmulator.Api/AisEmulator.Api.csproj
COPY src/AisEmulator.Api/ src/AisEmulator.Api/
RUN dotnet publish src/AisEmulator.Api/AisEmulator.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AisEmulator.Api.dll"]
