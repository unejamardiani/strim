FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first to leverage cache
COPY api/api.csproj api/
RUN dotnet restore api/api.csproj

# Copy the full source
COPY . .

# Place static frontend assets into api/wwwroot so they get published with the API
RUN mkdir -p api/wwwroot && \
    cp index.html style.css main.js filter-worker.js api/wwwroot/ && \
    cp features.html how-to-use.html comparison.html api/wwwroot/ && \
    cp 404.html 500.html 50x.html api/wwwroot/ && \
    cp -r blog api/wwwroot/

# Publish
RUN dotnet publish api/api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "api.dll"]
