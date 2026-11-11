#!/usr/bin/env bash
# Sends one signed test webhook to a local copy of the relay.
#
# Usage: ./scripts/send-webhook.sh [delivery-id] [json-payload]
# Uses the "demo" source and secret from appsettings.Development.json.

BASE_URL="${BASE_URL:-http://localhost:5116}"
SOURCE="${SOURCE:-demo}"
SECRET="${SECRET:-dev-secret}"

DELIVERY_ID="${1:-test-$(date +%s)}"
PAYLOAD="$2"
if [ -z "$PAYLOAD" ]; then
  PAYLOAD='{"message":"hello"}'
fi

# Same thing the server does: HMAC-SHA256 of the raw body, as hex
SIGNATURE=$(printf '%s' "$PAYLOAD" | openssl dgst -sha256 -hmac "$SECRET" | sed 's/^.*= //')

curl -s -i -X POST "$BASE_URL/webhooks/$SOURCE" \
  -H "Content-Type: application/json" \
  -H "X-Delivery-Id: $DELIVERY_ID" \
  -H "X-Signature-256: sha256=$SIGNATURE" \
  --data-raw "$PAYLOAD"
echo
