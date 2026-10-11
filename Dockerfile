FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first to leverage cache
COPY api/api.csproj api/
RUN dotnet restore api/api.csproj

# Copy the full source
COPY . .

# Place the frontend (landing, app, guides, blog) into api/wwwroot so it is published with the API.
RUN mkdir -p api/wwwroot && \
    cp index.html app.html features.html how-to-use.html comparison.html 404.html 500.html 50x.html api/wwwroot/ && \
    cp theme.css app.css site.css main.js robots.txt sitemap.xml api/wwwroot/ && \
    cp -r fonts blog api/wwwroot/

# Version every stylesheet and script by content hash so a cached old file is never paired with a
# new page. Matches "x.css", "../x.css" and "/x.css" in every page.
RUN cd api/wwwroot && \
    pages=$(find . -name '*.html') && \
    for f in theme.css app.css site.css main.js; do \
      v=$(sha256sum "$f" | cut -c1-12); \
      sed -i -E "s#([\"/])$f\"#\1$f?v=$v\"#g" $pages; \
    done && \
    ! grep -l -E '(theme|app|site)\.css"|main\.js"' $pages && \
    grep -q 'main.js?v=' app.html

# Publish
RUN dotnet publish api/api.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "api.dll"]
