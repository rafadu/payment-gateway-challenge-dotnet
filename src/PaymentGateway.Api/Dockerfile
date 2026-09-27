# Multi-stage build for the Payment Gateway API.
#
# Build stage: copies the project, then runs `dotnet publish` (which itself does the NuGet
# restore + compile + publish in one step). Splitting restore into its own RUN step doesn't
# help here — Docker layers persist across RUN steps, but `--no-restore` is fragile when the
# COPY that brings the source code happens to overwrite part of the restored obj/ tree. The
# BuildKit cache mount speeds up rebuilds by caching the NuGet packages between invocations.
#syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/PaymentGateway.Api/ src/PaymentGateway.Api/
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/PaymentGateway.Api/PaymentGateway.Api.csproj \
        -c Release \
        -o /app/publish \
        /p:UseAppHost=false

# Runtime stage: only the ASP.NET 10 base image, plus the published artefact. No SDK, no
# build cache — small surface area.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

# Container listens on 8080; docker-compose maps host 5067 → container 8080.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# The bank simulator and MongoDB URLs default to `localhost` in appsettings.json for non-Docker
# development. docker-compose overrides both via env vars below.
ENV \
    BankSimulator__BaseUrl=http://bank_simulator:8080 \
    Mongo__ConnectionString=mongodb://mongo:27017

ENTRYPOINT ["dotnet", "PaymentGateway.Api.dll"]