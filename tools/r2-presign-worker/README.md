# R2 Presign Worker

Cloudflare Worker that generates presigned PUT URLs for uploading recording ZIPs to R2.

## Setup

### 1. Create R2 bucket

```bash
wrangler r2 bucket create quest-recordings
```

### 2. Create R2 API token

Go to Cloudflare Dashboard → R2 → Manage R2 API Tokens → Create API Token.
Grant **Object Read & Write** on the `quest-recordings` bucket.

### 3. Configure secrets

```bash
cd tools/r2-presign-worker
npm install

wrangler secret put R2_ACCESS_KEY_ID
wrangler secret put R2_SECRET_ACCESS_KEY
wrangler secret put R2_ACCOUNT_ID

# Optional: require Bearer token auth from the Unity client
wrangler secret put AUTH_TOKEN
```

You can also set `R2_BUCKET_NAME` if different from `quest-recordings`.

### 4. Deploy

```bash
npm run deploy
```

## API

```
GET /?filename=20260306_142530.zip
Authorization: Bearer <AUTH_TOKEN>   (if configured)

200: { "upload_url": "https://...signed-url", "key": "recordings/20260306_142530.zip" }
400: { "error": "Missing filename parameter" }
401: { "error": "Unauthorized" }
500: { "error": "Signing failed: ..." }
```

## Testing

```bash
# Start local dev server
npm run dev

# Test presign
curl "http://localhost:8787/?filename=test.zip"

# Upload a file using the returned URL
curl -X PUT "<upload_url>" --data-binary @test.zip
```

## Unity Configuration

In the Unity Inspector on the R2Uploader component:
- **Presign Endpoint**: `https://r2-presign-worker.<your-subdomain>.workers.dev`
- **Auth Token**: the AUTH_TOKEN value you set (if any)
