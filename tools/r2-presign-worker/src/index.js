import { AwsClient } from "aws4fetch";

export default {
  async fetch(request, env) {
    // CORS preflight
    if (request.method === "OPTIONS") {
      return new Response(null, {
        headers: corsHeaders(),
      });
    }

    if (request.method !== "GET") {
      return jsonResponse({ error: "Method not allowed" }, 405);
    }

    // Auth check
    if (env.AUTH_TOKEN) {
      const auth = request.headers.get("Authorization");
      if (auth !== `Bearer ${env.AUTH_TOKEN}`) {
        return jsonResponse({ error: "Unauthorized" }, 401);
      }
    }

    const url = new URL(request.url);
    const filename = url.searchParams.get("filename");

    if (!filename) {
      return jsonResponse({ error: "Missing filename parameter" }, 400);
    }

    // Sanitize filename — allow alphanumeric, underscores, hyphens, dots
    if (!/^[\w\-\.]+$/.test(filename)) {
      return jsonResponse({ error: "Invalid filename" }, 400);
    }

    const key = `recordings/${filename}`;

    try {
      const r2 = new AwsClient({
        accessKeyId: env.R2_ACCESS_KEY_ID,
        secretAccessKey: env.R2_SECRET_ACCESS_KEY,
      });

      const r2Url = new URL(
        `https://${env.R2_ACCOUNT_ID}.r2.cloudflarestorage.com/${env.R2_BUCKET_NAME || "quest-recordings"}/${key}`
      );

      // Sign a PUT URL with 1-hour TTL
      const signed = await r2.sign(
        new Request(r2Url, {
          method: "PUT",
        }),
        {
          aws: { signQuery: true },
          headers: {
            "X-Amz-Expires": "3600",
          },
        }
      );

      return jsonResponse(
        {
          upload_url: signed.url,
          key: key,
        },
        200
      );
    } catch (err) {
      return jsonResponse({ error: `Signing failed: ${err.message}` }, 500);
    }
  },
};

function jsonResponse(body, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "Content-Type": "application/json",
      ...corsHeaders(),
    },
  });
}

function corsHeaders() {
  return {
    "Access-Control-Allow-Origin": "*",
    "Access-Control-Allow-Methods": "GET, OPTIONS",
    "Access-Control-Allow-Headers": "Authorization",
  };
}
