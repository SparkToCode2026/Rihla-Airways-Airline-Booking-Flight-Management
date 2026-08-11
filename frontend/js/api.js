// ============================================================
// the single place every API call goes through.
//
// why bother instead of just calling fetch() in each page:
//  - the jwt has to go on EVERY protected request. one wrapper
//    means nobody forgets it, and nobody writes it differently
//  - our api returns errors as plain text (Conflict("Seat 14C is
//    already taken")) not json, so error handling needs a helper
//    or every page reinvents it
//  - when a token expires, one place decides what happens
// ============================================================

const API_BASE = "http://localhost:5251/api";

// ---------- token storage ----------
// localStorage, not a cookie. a cookie would be sent automatically on
// every request which sounds convenient but opens us to CSRF - the
// whole point of a bearer token is that the client attaches it
// deliberately. downside is XSS can read localStorage, which is why
// nothing sensitive beyond the token goes in here
const TOKEN_KEY = "rihla_token";
const USER_KEY = "rihla_user";

const auth = {
  getToken: () => localStorage.getItem(TOKEN_KEY),

  getUser: () => {
    const raw = localStorage.getItem(USER_KEY);
    return raw ? JSON.parse(raw) : null;
  },

  // called by login.js after a successful POST /api/auth/login
  save(loginResponse) {
    localStorage.setItem(TOKEN_KEY, loginResponse.token);
    localStorage.setItem(USER_KEY, JSON.stringify({
      userId: loginResponse.userId,
      name: loginResponse.name,
      email: loginResponse.email,
      role: loginResponse.role
    }));
  },

  clear() {
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(USER_KEY);
  },

  isLoggedIn: () => !!localStorage.getItem(TOKEN_KEY),

  // handy for hiding admin-only buttons. note this is a UI convenience
  // ONLY - the server checks roles itself on every request, so a user
  // editing localStorage to say "Admin" still gets a 403. never rely
  // on a client-side role check for actual security
  isAdmin() {
    const u = this.getUser();
    return u?.role === "Admin";
  },

  isStaff() {
    const u = this.getUser();
    return u?.role === "Admin" || u?.role === "Staff";
  }
};


// ---------- the error type ----------
// a plain Error loses the status code, and we need it - 409 means
// "your request was valid but the business rule said no" and should
// show the server's message, while 500 means something broke and
// shouldn't show the user a raw stack trace
class ApiError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}


// ---------- the core request function ----------
async function request(method, path, body = null) {
  const headers = {};

  // only set Content-Type when there's actually a body. sending it on a
  // GET makes some servers reject the request outright
  if (body) headers["Content-Type"] = "application/json";

  const token = auth.getToken();
  if (token) headers["Authorization"] = `Bearer ${token}`;

  let res;
  try {
    res = await fetch(`${API_BASE}${path}`, {
      method,
      headers,
      body: body ? JSON.stringify(body) : null
    });
  } catch (networkError) {
    // fetch only rejects on NETWORK failure - a 404 or 500 resolves
    // normally. so this branch means the api isn't running, not that
    // the request was rejected. worth its own message because "failed
    // to fetch" tells a teammate nothing
    throw new ApiError(0,
      "Cannot reach the API. Is the backend running on port 5251?");
  }

  // 204 No Content - our DELETE endpoints return this. calling
  // res.json() on an empty body throws, so bail out first
  if (res.status === 204) return null;

  if (!res.ok) {
    // our controllers return errors in two shapes:
    //  - Conflict("Seat 14C is already taken")  -> plain text
    //  - [ApiController] validation failures    -> a json problem
    //    details object with an "errors" dictionary
    // so we read as text first and only try to parse it as json after
    const raw = await res.text();
    let message = raw;

    try {
      const parsed = JSON.parse(raw);

      if (parsed.errors) {
        // flatten the validation dictionary into one readable string:
        // { "Email": ["is required"], "Name": ["too long"] }
        // becomes "Email: is required. Name: too long"
        message = Object.entries(parsed.errors)
          .map(([field, msgs]) => `${field}: ${msgs.join(", ")}`)
          .join(" · ");
      } else if (parsed.title) {
        message = parsed.title;
      }
    } catch {
      // not json - it was already a plain string, keep it as is
    }

    // 401 means the token is missing, expired or invalid. clear it and
    // send them to login, otherwise they sit on a page that silently
    // fails every request. the redirect happens BEFORE the throw so
    // the calling page doesn't also try to show an error toast
    if (res.status === 401) {
      auth.clear();
      if (!location.pathname.endsWith("login.html")) {
        location.href = "login.html";
      }
    }

    throw new ApiError(res.status, message || `Request failed (${res.status})`);
  }

  return res.json();
}


// ---------- what pages actually call ----------
const api = {
  get: (path) => request("GET", path),
  post: (path, body) => request("POST", path, body),
  put: (path, body) => request("PUT", path, body),
  patch: (path, body) => request("PATCH", path, body),
  del: (path) => request("DELETE", path)
};


// ---------- shared UI helpers ----------
// every page needs these, so they live here rather than being
// copy-pasted into thirteen files

// show a bootstrap alert in a container. pages put
// <div id="alert"></div> near the top and call this
function showAlert(message, type = "danger", containerId = "alert") {
  const box = document.getElementById(containerId);
  if (!box) return;

  box.innerHTML = `
    <div class="alert alert-${type} alert-dismissible fade show" role="alert">
      ${escapeHtml(message)}
      <button type="button" class="btn-close" data-bs-dismiss="alert"></button>
    </div>`;

  // success messages disappear on their own, errors stay until dismissed -
  // if something went wrong the user should have time to read why
  if (type === "success") {
    setTimeout(() => { box.innerHTML = ""; }, 4000);
  }
}

// ALWAYS use this before putting API data into innerHTML. a passenger
// name of <script>alert(1)</script> would otherwise execute - that's
// stored XSS, and it's the exact thing we spent the backend guarding
// the database against
function escapeHtml(value) {
  if (value === null || value === undefined) return "";
  const div = document.createElement("div");
  div.textContent = String(value);
  return div.innerHTML;
}

// "2026-08-22T16:00:00" -> "22 Aug 2026, 16:00"
function formatDate(iso) {
  if (!iso) return "—";
  const d = new Date(iso);
  return d.toLocaleString("en-GB", {
    day: "2-digit", month: "short", year: "numeric",
    hour: "2-digit", minute: "2-digit"
  });
}

// 214.6 -> "214.60 OMR"
function formatMoney(amount) {
  if (amount === null || amount === undefined) return "—";
  return `${Number(amount).toFixed(2)} OMR`;
}

// colour-coded status pills. one function so Confirmed looks the same
// on the bookings page as it does on the payments page
function statusBadge(status) {
  const map = {
    Confirmed: "success", Completed: "success", Landed: "success",
    Pending: "warning", Delayed: "warning", Boarding: "info",
    Scheduled: "primary", Departed: "info",
    Cancelled: "danger", Failed: "danger", Refunded: "secondary"
  };
  const colour = map[status] || "secondary";
  return `<span class="badge text-bg-${colour}">${escapeHtml(status)}</span>`;
}

// pages call this at the top of their init. anything behind [Authorize]
// on the server should call it, so the user gets sent to login instead
// of watching every request fail
function requireLogin() {
  if (!auth.isLoggedIn()) {
    location.href = "login.html";
    return false;
  }
  return true;
}