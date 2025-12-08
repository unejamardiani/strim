# Production Build Guide

## ⚠️ CRITICAL: Do NOT Deploy with Tailwind CDN

The current HTML files use Tailwind CSS CDN, which is **NOT SAFE FOR PRODUCTION**:

```html
<!-- ❌ DEVELOPMENT ONLY - DO NOT USE IN PRODUCTION -->
<script src="https://cdn.tailwindcss.com"></script>
```

**Security Risks:**
- No Subresource Integrity (SRI) protection
- CDN compromise could inject malicious code
- Requires 'unsafe-inline' in CSP
- Slower performance (runtime compilation)
- Larger file size

**This guide shows how to build production-ready HTML files.**

---

## Quick Start (Recommended)

### Step 1: Install Dependencies

```bash
npm init -y
npm install -D tailwindcss postcss autoprefixer
npx tailwindcss init
```

### Step 2: Configure Tailwind

Create `tailwind.config.js`:

```javascript
/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    "./index.html",
    "./features.html",
    "./how-to-use.html",
    "./comparison.html",
    "./blog/**/*.html",
  ],
  theme: {
    extend: {
      fontFamily: {
        sans: ['Space Grotesk', 'Inter', 'system-ui', 'sans-serif'],
      },
      colors: {
        'zone-input': '#4ac9ff',
        'zone-filter': '#6df0c2',
        'zone-output': '#00ff9f',
      },
      animation: {
        'pulse-glow': 'pulseGlow 8s ease-in-out infinite',
        'flow-arrow': 'flowArrow 2s ease-in-out infinite',
        'flow-arrow-vertical': 'flowArrowVertical 2s ease-in-out infinite',
      },
    },
  },
  plugins: [],
}
```

### Step 3: Create Input CSS

Create `src/input.css`:

```css
@tailwind base;
@tailwind components;
@tailwind utilities;

/* Custom animations from style.css */
@layer utilities {
  @keyframes pulseGlow {
    0%, 100% { opacity: 0.3; }
    50% { opacity: 0.6; }
  }

  @keyframes flowArrow {
    0%, 100% { transform: translateX(-10px); opacity: 0; }
    50% { opacity: 1; }
  }

  @keyframes flowArrowVertical {
    0%, 100% { transform: translateY(-10px); opacity: 0; }
    50% { opacity: 1; }
  }
}
```

### Step 4: Build CSS

```bash
# Development build
npx tailwindcss -i ./src/input.css -o ./dist/output.css --watch

# Production build (minified)
npx tailwindcss -i ./src/input.css -o ./dist/output.css --minify
```

### Step 5: Update HTML Files

**Before (Development - INSECURE):**
```html
<script src="https://cdn.tailwindcss.com"></script>
<script>
  tailwind.config = {
    theme: {
      extend: {
        fontFamily: {
          sans: ['Space Grotesk', 'Inter', 'system-ui', 'sans-serif'],
        },
        colors: {
          'zone-input': '#4ac9ff',
          'zone-filter': '#6df0c2',
          'zone-output': '#00ff9f',
        },
      },
    },
  }
</script>
<link rel="stylesheet" href="style.css" />
```

**After (Production - SECURE):**
```html
<link rel="stylesheet" href="dist/output.css" />
<link rel="stylesheet" href="style.css" />
```

### Step 6: Update CSP (Remove 'unsafe-inline')

**Before:**
```html
<meta http-equiv="Content-Security-Policy" content="default-src 'self'; script-src 'self' 'unsafe-inline' https://cdn.tailwindcss.com; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.tailwindcss.com; ...">
```

**After:**
```html
<meta http-equiv="Content-Security-Policy" content="default-src 'self'; script-src 'self'; style-src 'self' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; img-src 'self' data: https:; connect-src 'self' https:;">
```

**Note:** We can remove `'unsafe-inline'` and `https://cdn.tailwindcss.com` entirely!

---

## File Structure

```
strim/
├── src/
│   └── input.css          # Tailwind input file
├── dist/
│   └── output.css         # Built CSS (add to .gitignore)
├── index.html             # Update to use dist/output.css
├── features.html          # Update to use dist/output.css
├── how-to-use.html        # Update to use dist/output.css
├── comparison.html        # Update to use dist/output.css
├── blog/
│   ├── index.html         # Update to use ../dist/output.css
│   └── *.html             # Update to use ../dist/output.css
├── tailwind.config.js     # Tailwind configuration
├── package.json
└── .gitignore             # Add dist/ to gitignore
```

---

## Deployment

### Option 1: Build Locally, Deploy Files

```bash
# Build production CSS
npm run build

# Deploy all files including dist/output.css
az staticwebapp deploy # or your deployment method
```

### Option 2: Build in CI/CD

**GitHub Actions (.github/workflows/deploy.yml):**

```yaml
name: Deploy
on:
  push:
    branches: [main]

jobs:
  build-and-deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3

      - name: Setup Node.js
        uses: actions/setup-node@v3
        with:
          node-version: '18'

      - name: Install dependencies
        run: npm ci

      - name: Build Tailwind CSS
        run: npm run build

      - name: Deploy to Azure Static Web Apps
        uses: Azure/static-web-apps-deploy@v1
        with:
          azure_static_web_apps_api_token: ${{ secrets.AZURE_STATIC_WEB_APPS_API_TOKEN }}
          repo_token: ${{ secrets.GITHUB_TOKEN }}
          action: "upload"
          app_location: "/"
          output_location: "/"
```

**Add to package.json:**
```json
{
  "scripts": {
    "dev": "tailwindcss -i ./src/input.css -o ./dist/output.css --watch",
    "build": "tailwindcss -i ./src/input.css -o ./dist/output.css --minify"
  },
  "devDependencies": {
    "tailwindcss": "^3.4.0",
    "autoprefixer": "^10.4.16",
    "postcss": "^8.4.32"
  }
}
```

---

## Benefits of Production Build

| Metric | CDN (Dev) | Built CSS (Prod) |
|--------|-----------|------------------|
| **Security** | ❌ No SRI, requires unsafe-inline | ✅ No external scripts, no unsafe-inline |
| **Performance** | ❌ ~300KB + runtime compilation | ✅ ~10-30KB minified, pre-compiled |
| **Reliability** | ❌ Depends on CDN uptime | ✅ Self-hosted, always available |
| **Caching** | ⚠️ CDN cache (shared) | ✅ Your own cache control |
| **Bundle Size** | ❌ Includes all Tailwind utilities | ✅ Only used utilities |

---

## Testing Production Build

### 1. Build CSS
```bash
npm run build
```

### 2. Serve Locally
```bash
python3 -m http.server 8000
```

### 3. Test in Browser
- Open http://localhost:8000
- Open DevTools → Network tab
- Verify `dist/output.css` loads (not cdn.tailwindcss.com)
- Check Console for CSP violations (should be none)

### 4. Verify File Size
```bash
ls -lh dist/output.css
# Should be ~10-30KB (much smaller than 300KB CDN)
```

---

## Migration Script

Use this script to automatically convert all HTML files from CDN to built CSS:

**migrate-to-production.sh:**
```bash
#!/bin/bash

# Files to update
FILES="index.html features.html how-to-use.html comparison.html blog/index.html blog/*.html"

for file in $FILES; do
  if [ -f "$file" ]; then
    echo "Updating $file..."

    # Remove Tailwind CDN script and config
    sed -i '/<script src="https:\/\/cdn.tailwindcss.com"><\/script>/,/<\/script>/d' "$file"

    # Add production CSS link before style.css
    sed -i 's|<link rel="stylesheet" href="style.css" />|<link rel="stylesheet" href="dist/output.css" />\n    <link rel="stylesheet" href="style.css" />|' "$file"

    # For blog files, use relative path
    if [[ $file == blog/* ]]; then
      sed -i 's|href="dist/output.css"|href="../dist/output.css"|' "$file"
    fi

    # Update CSP - remove unsafe-inline and Tailwind CDN
    sed -i "s|script-src 'self' 'unsafe-inline' https://cdn.tailwindcss.com|script-src 'self'|" "$file"
    sed -i "s|style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.tailwindcss.com|style-src 'self' https://fonts.googleapis.com|" "$file"

    echo "✅ Updated $file"
  fi
done

echo ""
echo "✅ Migration complete!"
echo "Next steps:"
echo "1. Run: npm run build"
echo "2. Test locally: python3 -m http.server 8000"
echo "3. Deploy to production"
```

**Usage:**
```bash
chmod +x migrate-to-production.sh
./migrate-to-production.sh
npm run build
```

---

## Troubleshooting

### Issue: Styles not applying after build
**Solution:** Ensure `content` paths in `tailwind.config.js` match your HTML files.

### Issue: Custom animations not working
**Solution:** Copy animation keyframes from `style.css` to `src/input.css`.

### Issue: File too large
**Solution:** Check that you're using `--minify` flag and `content` paths are correct.

### Issue: CSP violations after migration
**Solution:** Verify you removed `'unsafe-inline'` and CDN URLs from CSP.

---

## Production Checklist

Before deploying to production:

- [ ] Install Tailwind via npm (`npm install -D tailwindcss`)
- [ ] Create `tailwind.config.js` with custom theme
- [ ] Create `src/input.css` with Tailwind directives
- [ ] Build CSS (`npm run build`)
- [ ] Update all HTML files to use `dist/output.css`
- [ ] Remove Tailwind CDN `<script>` tags
- [ ] Remove inline `tailwind.config` scripts
- [ ] Update CSP to remove `'unsafe-inline'` and CDN URLs
- [ ] Add `dist/` to `.gitignore` or commit built CSS
- [ ] Test locally before deploying
- [ ] Set up CI/CD to build CSS automatically
- [ ] Verify no CSP violations in production
- [ ] Run security scan (Mozilla Observatory)

---

## Additional Security: Subresource Integrity (SRI)

If you commit the built CSS to git, you can add SRI hashes:

```bash
# Generate SRI hash
cat dist/output.css | openssl dgst -sha384 -binary | openssl base64 -A

# Example output:
# sha384-oqVuAfXRKap7fdgcCY5uykM6+R9GqQ8K/uxy9rx7HNQlGYl1kPzQho1wx4JwY8wC
```

**In HTML:**
```html
<link
  rel="stylesheet"
  href="dist/output.css"
  integrity="sha384-oqVuAfXRKap7fdgcCY5uykM6+R9GqQ8K/uxy9rx7HNQlGYl1kPzQho1wx4JwY8wC"
  crossorigin="anonymous"
/>
```

---

## Summary

**Current (Development):**
- ❌ Tailwind CDN (insecure)
- ❌ Requires 'unsafe-inline'
- ❌ No SRI protection
- ❌ 300KB+ payload
- ❌ Runtime compilation

**After This Guide (Production):**
- ✅ Self-hosted CSS (secure)
- ✅ No 'unsafe-inline' needed
- ✅ Optional SRI hashes
- ✅ 10-30KB payload
- ✅ Pre-compiled

**Estimated migration time:** 15-30 minutes

---

**Last Updated:** 2025-12-05
**Status:** Production build guide for secure deployment
