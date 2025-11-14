# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5142

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY backend/Directory.Build.props ./
COPY backend/src/Strim.Api/Strim.Api.csproj backend/src/Strim.Api/
COPY backend/tests/Strim.Tests/Strim.Tests.csproj backend/tests/Strim.Tests/
RUN dotnet restore backend/src/Strim.Api/Strim.Api.csproj
COPY . .
RUN dotnet publish backend/src/Strim.Api/Strim.Api.csproj -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Strim.Api.dll"]
