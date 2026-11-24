FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first to leverage cache
COPY api/api.csproj api/
RUN dotnet restore api/api.csproj

# Copy the full source
COPY . .

# Place the frontend (landing, app, guides) into api/wwwroot so it is published with the API.
# Every stylesheet and script in the repository root is shipped, so a new front-end file needs no
# change here.
RUN mkdir -p api/wwwroot && \
    cp index.html app.html features.html how-to-use.html comparison.html 404.html 500.html 50x.html api/wwwroot/ && \
    cp *.css *.js robots.txt sitemap.xml tmdb-logo.svg api/wwwroot/ && \
    cp -r fonts api/wwwroot/

# Version every stylesheet and script by content hash so a cached old file is never paired with a
# new page. Matches "x.css", "../x.css" and "/x.css" in every page. Only references in pages are
# versioned, so every script must be loaded by its own tag in a page: the build fails if a page
# still names an unversioned stylesheet or script, or if a script loads another local script.
RUN cd api/wwwroot && \
    pages=$(find . -name '*.html') && \
    for f in *.css *.js; do \
      v=$(sha256sum "$f" | cut -c1-12); \
      sed -i -E "s#([\"/])$f\"#\1$f?v=$v\"#g" $pages; \
    done && \
    ! grep -l -E '["/][A-Za-z0-9_.-]+\.(css|js)"' $pages && \
    ! grep -l -E "(from|import)[[:space:]]*\(?[[:space:]]*['\"]\.{1,2}/" *.js && \
    grep -q 'main.js?v=' app.html

# Publish
RUN dotnet publish api/api.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
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
