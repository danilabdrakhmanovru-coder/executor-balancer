# Сборка из корня репозитория: docker build -f src/ExecutorBalancer.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ExecutorBalancer.Domain/ExecutorBalancer.Domain.csproj src/ExecutorBalancer.Domain/
COPY src/ExecutorBalancer.Application/ExecutorBalancer.Application.csproj src/ExecutorBalancer.Application/
COPY src/ExecutorBalancer.Infrastructure/ExecutorBalancer.Infrastructure.csproj src/ExecutorBalancer.Infrastructure/
COPY src/ExecutorBalancer.Api/ExecutorBalancer.Api.csproj src/ExecutorBalancer.Api/
RUN dotnet restore src/ExecutorBalancer.Api/ExecutorBalancer.Api.csproj
COPY src/ src/
RUN dotnet publish src/ExecutorBalancer.Api/ExecutorBalancer.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=1
EXPOSE 8080
# непривилегированный пользователь из базового образа
USER $APP_UID
ENTRYPOINT ["dotnet", "ExecutorBalancer.Api.dll"]
