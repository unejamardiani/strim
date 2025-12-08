# Security Considerations

## External Dependencies

### Current Setup

Strim uses the following external dependencies for performance and ease of development:

1. **Google Fonts (fonts.googleapis.com / fonts.gstatic.com)**
   - Used for: Space Grotesk font family
   - Risk: Supply chain attack if Google's CDN is compromised
   - Mitigation: Content Security Policy (CSP) restricts sources

2. **Tailwind CSS CDN (cdn.tailwindcss.com)**
   - Used for: CSS framework (development convenience)
   - Risk: Supply chain attack if CDN is compromised
   - Mitigation: Content Security Policy (CSP) + should be replaced in production

### Content Security Policy

All HTML pages include a CSP meta tag that:

```html
<meta http-equiv="Content-Security-Policy" content="
  default-src 'self';
  script-src 'self' 'unsafe-inline' https://cdn.tailwindcss.com;
  style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.tailwindcss.com;
  font-src 'self' https://fonts.gstatic.com;
  img-src 'self' data: https:;
  connect-src 'self' https:;">
```

**What this does:**
- ✅ Restricts script execution to same origin + Tailwind CDN
- ✅ Restricts styles to same origin + Google Fonts + Tailwind CDN
- ✅ Restricts fonts to same origin + Google Fonts static
- ✅ Allows images from same origin, data URIs, and HTTPS sources
- ✅ Allows connections to same origin and HTTPS endpoints

**Note:** `'unsafe-inline'` is required for Tailwind CDN's runtime compilation. This should be removed in production.

### Why SRI (Subresource Integrity) Isn't Used

**Google Fonts:**
- Google Fonts serves different CSS files based on browser user-agent
- SRI hashes would break for different browsers
- CSP provides adequate protection

**Tailwind CDN:**
- Should be replaced with a build process in production
- Not intended for production use

## Recommended Production Improvements

### Priority 1: Remove Tailwind CDN

**Current (Development):**
```html
<script src="https://cdn.tailwindcss.com"></script>
```

**Recommended (Production):**
```bash
# Install Tailwind via npm
npm install -D tailwindcss
npx tailwindcss init

# Build CSS file
npx tailwindcss -i ./src/input.css -o ./dist/output.css --minify
```

**Benefits:**
- ✅ No external script dependency
- ✅ Smaller CSS file (only used utilities)
- ✅ Can remove 'unsafe-inline' from CSP
- ✅ Better performance
- ✅ No runtime compilation

### Priority 2: Self-Host Google Fonts (Optional)

**Current:**
```html
<link href="https://fonts.googleapis.com/css2?family=Space+Grotesk..." rel="stylesheet" />
```

**Option A: Self-host fonts**
```bash
# Download fonts
wget https://fonts.google.com/download?family=Space%20Grotesk

# Reference locally
@font-face {
  font-family: 'Space Grotesk';
  src: url('/fonts/space-grotesk.woff2') format('woff2');
}
```

**Option B: Keep Google Fonts (acceptable)**
- Google Fonts is generally trusted
- CSP restricts to fonts.googleapis.com only
- Risk is low compared to convenience

### Priority 3: Server-Side CSP Headers

**Current:** CSP via meta tag
**Recommended:** CSP via HTTP headers

**Add to server configuration:**

**Nginx:**
```nginx
add_header Content-Security-Policy "default-src 'self'; script-src 'self'; style-src 'self' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; img-src 'self' data: https:; connect-src 'self' https:;" always;
```

**Apache:**
```apache
Header always set Content-Security-Policy "default-src 'self'; script-src 'self'; style-src 'self' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; img-src 'self' data: https:; connect-src 'self' https:;"
```

**Azure Static Web Apps (staticwebapp.config.json):**
```json
{
  "globalHeaders": {
    "Content-Security-Policy": "default-src 'self'; script-src 'self'; style-src 'self' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; img-src 'self' data: https:; connect-src 'self' https:;"
  }
}
```

### Priority 4: Additional Security Headers

Add these headers in production:

```nginx
# Prevent clickjacking
add_header X-Frame-Options "SAMEORIGIN" always;

# Prevent MIME type sniffing
add_header X-Content-Type-Options "nosniff" always;

# Enable XSS protection (legacy browsers)
add_header X-XSS-Protection "1; mode=block" always;

# Force HTTPS
add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;

# Referrer policy
add_header Referrer-Policy "strict-origin-when-cross-origin" always;

# Permissions policy
add_header Permissions-Policy "geolocation=(), microphone=(), camera=()" always;
```

## Current Security Status

### ✅ Implemented
- Content Security Policy on all pages
- External resources restricted to trusted CDNs
- HTTPS connections enforced via CSP
- No inline scripts except Tailwind config (temporary)

### ⚠️ Development-Only (Should Fix for Production)
- Tailwind CDN (use build process instead)
- 'unsafe-inline' in CSP (can remove after Tailwind build)
- CSP via meta tag (use HTTP headers instead)

### 🔒 Optional Enhancements
- Self-host Google Fonts
- Add additional security headers
- Implement SRI for any local scripts
- Set up automated security scanning

## Testing Security

### Test CSP
1. Open browser DevTools → Console
2. Look for CSP violation errors
3. All resources should load without violations

### Test External Dependencies
```bash
# Check what external resources are loaded
curl -s https://yourdomain.com | grep -E '(src=|href=).*https://'
```

### Security Scan
```bash
# Use Mozilla Observatory
https://observatory.mozilla.org/

# Use SecurityHeaders.com
https://securityheaders.com/
```

## Reporting Security Issues

If you discover a security vulnerability:

1. **Do NOT** open a public issue
2. Email the maintainer directly: [maintainer email]
3. Include:
   - Description of the vulnerability
   - Steps to reproduce
   - Potential impact
   - Suggested fix (if available)

## Security Checklist for Production

- [ ] Replace Tailwind CDN with build process
- [ ] Remove 'unsafe-inline' from CSP
- [ ] Move CSP to HTTP headers
- [ ] Add additional security headers (X-Frame-Options, etc.)
- [ ] Enable HTTPS with HSTS
- [ ] Consider self-hosting Google Fonts
- [ ] Run security scan (Observatory, SecurityHeaders)
- [ ] Set up automated dependency scanning
- [ ] Configure proper CORS for API endpoints
- [ ] Review and restrict API rate limits
- [ ] Enable security logging and monitoring

## References

- [Content Security Policy (MDN)](https://developer.mozilla.org/en-US/docs/Web/HTTP/CSP)
- [Subresource Integrity (MDN)](https://developer.mozilla.org/en-US/docs/Web/Security/Subresource_Integrity)
- [OWASP Secure Headers Project](https://owasp.org/www-project-secure-headers/)
- [Mozilla Observatory](https://observatory.mozilla.org/)
- [Google Fonts Best Practices](https://developers.google.com/fonts/docs/getting_started)

---

**Last Updated:** 2025-12-05
**Status:** Development configuration with basic CSP protection
