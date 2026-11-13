#!/usr/bin/env bash
# Checks a GitHub token against every endpoint the dashboard calls, BEFORE the first deployment.
#
#   read -rs GITHUB_TOKEN && export GITHUB_TOKEN        # type or paste the token; nothing is shown
#   scripts/check-github-token.sh --enterprise <enterprise-slug>
#   scripts/check-github-token.sh --org <organization-login>
#
# The token is read from the environment variable GITHUB_TOKEN only (never from an argument, so it is not in the process list
# or your shell history) and is never printed. Needs only bash and curl.
#
# One line per endpoint: HTTP status, the path (without query string) and a plain-English meaning. For a 200 the line also lists
# the top-level field names of the JSON answer, so you can compare them with docs/API-ASSUMPTIONS.md. Field VALUES are never
# printed (they contain names and usage data). For a non-200 answer GitHub's short error text is printed (it is an error
# message, not data). With --deep the line lists key paths at every depth (still names only): useful, but if GitHub ever keyed
# an object by user login those logins would show, so use it only on a screen nobody else sees.
#
# Exit code: 0 all endpoints answered 200; 1 at least one did not; 2 bad usage.
#
# Optional environment: GITHUB_API_URL (default https://api.github.com), GITHUB_API_VERSION (default 2026-03-10).
set +x   # never trace this script: tracing would print the token
set -u

api="${GITHUB_API_URL:-https://api.github.com}"; api="${api%/}"
version="${GITHUB_API_VERSION:-2026-03-10}"
scope="" ; slug="" ; deep=0

usage() {
  echo "Usage: GITHUB_TOKEN=... $0 (--enterprise <slug> | --org <login>) [--deep]" >&2
  echo "Set the token without echoing it:  read -rs GITHUB_TOKEN && export GITHUB_TOKEN" >&2
  exit 2
}

while [ $# -gt 0 ]; do
  case "$1" in
    --enterprise) scope=enterprise; slug="${2:-}"; shift 2 || usage ;;
    --org)        scope=org;        slug="${2:-}"; shift 2 || usage ;;
    --deep)       deep=1; shift ;;
    *) usage ;;
  esac
done
[ -n "$scope" ] && [ -n "$slug" ] || usage
case "$slug" in *[!A-Za-z0-9_.-]*|-*|.*) echo "The slug may only contain letters, digits, '-', '_' and '.'." >&2; exit 2 ;; esac
if [ -z "${GITHUB_TOKEN:-}" ]; then
  echo "GITHUB_TOKEN is not set. Run:  read -rs GITHUB_TOKEN && export GITHUB_TOKEN   (then paste the token and press Enter)" >&2
  exit 2
fi
case "$api" in
  https://*|http://localhost*|http://127.0.0.1*) ;;
  *) echo "Refusing to send a token over plain http to $api (only https, or http to localhost for testing)." >&2; exit 2 ;;
esac
command -v curl >/dev/null 2>&1 || { echo "curl is required." >&2; exit 2; }

tmp="$(mktemp -d)"; chmod 700 "$tmp"
trap 'rm -rf "$tmp"' EXIT

year="$(date -u +%Y)"; month="$(date -u +%-m)"

# Top-level (or, with --deep, every) JSON key name; values are never emitted. Reads the file given as $1.
json_keys() {
  awk -v deep="$deep" '
    { text = text $0 "\n" }
    END {
      n = length(text); depth = 0; instr = 0; esc = 0; buf = ""; lastkey = ""; count = 0
      for (i = 1; i <= n; i++) {
        c = substr(text, i, 1)
        if (instr) {
          if (esc) { esc = 0; buf = buf c }
          else if (c == "\\") { esc = 1; buf = buf c }
          else if (c == "\"") {
            instr = 0
            if (depth > 0 && typ[depth] == "o" && expect[depth]) {
              expect[depth] = 0
              lastkey = (pre[depth] == "" ? buf : pre[depth] "." buf)
              if ((deep == 1 || depth == 1) && !(lastkey in seen)) { seen[lastkey] = 1; out[++count] = lastkey }
            }
          } else buf = buf c
          continue
        }
        if (c == "\"") { instr = 1; buf = ""; continue }
        if (c == "{" || c == "[") {
          base = (depth == 0) ? "" : (typ[depth] == "o" ? lastkey : pre[depth])
          depth++; typ[depth] = (c == "{") ? "o" : "a"
          pre[depth] = (c == "[") ? base "[]" : base
          expect[depth] = (c == "{")
          if (depth == 1 && c == "[") rootarray = 1
        } else if (c == "}" || c == "]") { if (depth > 0) depth-- }
        else if (c == "," && depth > 0 && typ[depth] == "o") expect[depth] = 1
      }
      if (rootarray && deep != 1) { print "(JSON array at top level)"; exit }
      line = ""
      for (k = 1; k <= count && k <= 120; k++) line = line (k > 1 ? ", " : "") out[k]
      print (count == 0 ? "(no fields)" : line) (count > 120 ? ", ..." : "")
    }' "$1"
}

# GitHub error text: its "message" field only, one line, at most 120 characters, token removed.
error_text() {
  local m
  m="$(tr -d '\r\n' < "$1" | sed -n 's/.*"message"[[:space:]]*:[[:space:]]*"\(\([^"\\]\|\\.\)*\)".*/\1/p' | head -c 120)"
  m="${m//$GITHUB_TOKEN/***}"
  printf '%s' "$m" | tr -c '[:print:]' '?'
}

meaning() { # meaning <status> <scope>
  case "$1" in
    200) echo "OK: the token and this endpoint work." ;;
    401) echo "FIX THE TOKEN: GitHub rejected it (wrong, expired or revoked)." ;;
    403) if [ "$2" = org ]; then
           echo "FIX THE TOKEN or the scope: valid token but not allowed (missing scope or permission, not an organization owner or billing manager, SAML SSO not authorised), or this organization is billed through an enterprise (try --enterprise)."
         else
           echo "FIX THE TOKEN: valid token but not allowed (not a classic token, missing scope, not an enterprise owner or billing manager, or SAML SSO not authorised)."
         fi ;;
    404) if [ "$2" = org ]; then
           echo "NOT FOUND: wrong organization name, the token cannot see it, the organization is billed through an enterprise (try --enterprise), or the endpoint assumption is wrong (docs/API-ASSUMPTIONS.md)."
         else
           echo "NOT FOUND: wrong enterprise slug, the token cannot see the enterprise, or this endpoint path is a wrong assumption (docs/API-ASSUMPTIONS.md, enterprise scope)."
         fi ;;
    429) echo "RATE LIMITED: wait and run again." ;;
    000) echo "NO ANSWER: could not reach the host (network, proxy or GITHUB_API_URL)." ;;
    5??) echo "GITHUB ERROR: a server-side problem; run again later." ;;
    *)   echo "UNEXPECTED status; see docs/API-ASSUMPTIONS.md." ;;
  esac
}

failures=0; token_fix=0; path_fix=0; first=1

check() { # check <label> <path-with-optional-query>
  local label="$1" target="$2" status path="${2%%\?*}" hdr="$tmp/h" body="$tmp/b"
  : > "$hdr"; : > "$body"
  # The token goes to curl through its config on stdin, so it never appears in the process list.
  status="$(printf 'header = "Authorization: Bearer %s"\n' "$GITHUB_TOKEN" |
    curl -sS -K - --max-time 40 -o "$body" -D "$hdr" -w '%{http_code}' \
      -H "Accept: application/vnd.github+json" -H "X-GitHub-Api-Version: $version" -H "User-Agent: copilot-usage-token-check" \
      "$api$target" 2>/dev/null)" || status=000
  [ -n "$status" ] || status=000

  if [ "$first" = 1 ] && [ "$status" != 000 ]; then
    first=0
    local scopes; scopes="$(tr -d '\r' < "$hdr" | sed -n 's/^[Xx]-[Oo][Aa]uth-[Ss]copes:[[:space:]]*//p' | head -n1)"
    if [ -n "$scopes" ]; then echo "Token scopes reported by GitHub: $scopes"
    else echo "Token scopes reported by GitHub: (none; a fine-grained token, or the header was not sent)"; fi
  fi

  local extra=""
  if [ "$status" = 200 ]; then
    extra=" Fields: $(json_keys "$body")"
  else
    local msg; msg="$(error_text "$body")"
    [ -n "$msg" ] && extra=" GitHub says: \"$msg\"."
    if tr -d '\r' < "$hdr" | grep -qi '^x-github-sso:'; then extra="$extra SAML SSO: authorise this token for the organization or enterprise (token settings, Configure SSO)."; fi
  fi
  printf '%s  GET %s  [%s]  %s%s\n' "$status" "$path" "$label" "$(meaning "$status" "$scope")" "$extra"

  case "$status" in
    200) ;;
    401|403) failures=$((failures + 1)); token_fix=1 ;;
    404) failures=$((failures + 1)); path_fix=1 ;;
    *) failures=$((failures + 1)) ;;
  esac
}

echo "Checking scope: $scope ($slug) against $api (API version $version)"
if [ "$scope" = enterprise ]; then
  check "seats"              "/enterprises/$slug/copilot/billing/seats?per_page=1"
  check "ai credit usage"    "/enterprises/$slug/settings/billing/ai_credit/usage?year=$year&month=$month"
  check "metrics 28-day"     "/enterprises/$slug/copilot/metrics/reports/users-28-day/latest"
else
  check "seat summary"       "/orgs/$slug/copilot/billing"
  check "seats"              "/orgs/$slug/copilot/billing/seats?per_page=1"
  check "ai credit usage"    "/organizations/$slug/settings/billing/ai_credit/usage?year=$year&month=$month"
  check "metrics 28-day"     "/orgs/$slug/copilot/metrics/reports/users-28-day/latest"
fi

echo
if [ "$failures" = 0 ]; then
  echo "All endpoints answered 200. Compare the field names above with docs/API-ASSUMPTIONS.md; a renamed field shows up in the app as zeros, not as an error."
  exit 0
fi
[ "$token_fix" = 1 ] && echo "401 or 403 above: fix the token (type, scopes, owner or billing-manager role, SAML SSO). See docs/DEPLOY.md, section 1."
[ "$path_fix" = 1 ] && echo "404 above: check the slug first. If the seats line is 200 but another line is 404, that endpoint path is an unverified assumption: see docs/API-ASSUMPTIONS.md and GitHubEndpoints.cs."
echo "$failures endpoint(s) did not answer 200."
exit 1
