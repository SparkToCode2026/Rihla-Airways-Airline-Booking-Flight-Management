// ============================================================
// login, register and logout. the token storage itself lives in
// api.js (the `auth` object) - this file is only the page wiring
// ============================================================

// show/hide the password. flipping the input's type is the whole trick -
// the two icons are both in the DOM and CSS shows one at a time.
// aria-pressed is what a screen reader reads out, so it has to track the
// real state rather than just the icon
function wirePasswordToggle(inputId, buttonId) {
  const input = document.getElementById(inputId);
  const btn = document.getElementById(buttonId);
  if (!input || !btn) return;

  btn.addEventListener("click", () => {
    const nowVisible = input.type === "password";
    input.type = nowVisible ? "text" : "password";
    btn.classList.toggle("is-visible", nowVisible);
    btn.setAttribute("aria-pressed", String(nowVisible));
    btn.setAttribute("aria-label", nowVisible ? "Hide password" : "Show password");
    // keep the caret where it was - toggling type otherwise drops focus
    input.focus({ preventScroll: true });
  });
}


function initLogin() {
  // already signed in? skip the form. otherwise hitting back after
  // logging in dumps you on a login page while holding a valid token
  if (auth.isLoggedIn()) {
    location.href = "index.html";
    return;
  }
  

  const form = document.getElementById("loginForm");
  const btn = document.getElementById("submitBtn");

  wirePasswordToggle("password", "pwToggle");

  form.addEventListener("submit", async (e) => {
    // WITHOUT this the browser does a full page reload and the
    // request never reaches our fetch. easily the most common
    // bug in a plain-js form
    e.preventDefault();

    const email = document.getElementById("email").value.trim();
    const password = document.getElementById("password").value;

    if (!email || !password) {
      showAlert("Enter both your email and password.", "warning");
      return;
    }

    // disable while in flight - a double-click otherwise fires two
    // logins and the second overwrites the first token mid-redirect
    setBusy(btn, true, "Signing in…");

    try {
      const res = await api.post("/auth/login", { email, password });
      auth.save(res);
      location.href = "index.html";
    } catch (err) {
      // AuthController returns the SAME message for a wrong password
      // and an unknown email, on purpose - telling them apart lets
      // anyone probe which addresses are registered
      showAlert(err.message, "danger");
      setBusy(btn, false, "Sign In");
    }
  });
}


function initRegister() {
  if (auth.isLoggedIn()) {
    location.href = "index.html";
    return;
  }

  const form = document.getElementById("registerForm");
  const btn = document.getElementById("submitBtn");

  wirePasswordToggle("password", "pwToggle");

  form.addEventListener("submit", async (e) => {
    e.preventDefault();

    const name = document.getElementById("name").value.trim();
    const email = document.getElementById("email").value.trim();
    const password = document.getElementById("password").value;

    // client-side checks mirror the DTO attributes so the user gets
    // told immediately. the SERVER still validates - this is a
    // convenience, not a security boundary. anyone can skip it with
    // curl, which is exactly why RegisterDto has its own [Required]
    // and [MinLength] attributes
    if (!name || !email || !password) {
      showAlert("All fields are required.", "warning");
      return;
    }
    if (password.length < 8) {
      showAlert("Password must be at least 8 characters.", "warning");
      return;
    }

    setBusy(btn, true, "Creating account…");

    try {
      // register returns a token just like login does, so we can
      // sign them straight in rather than bouncing to the login page
      const res = await api.post("/auth/register", { name, email, password });
      auth.save(res);
      location.href = "index.html";
    } catch (err) {
      showAlert(err.message, "danger");
      setBusy(btn, false, "Create Account");
    }
  });
}


function logout() {
  auth.clear();
  location.href = "login.html";
}


// small helper so a button can't be clicked twice while a request
// is in flight, and the user can see something is happening
function setBusy(btn, busy, label) {
  btn.disabled = busy;
  btn.innerHTML = busy
    ? `<span class="spinner-border spinner-border-sm me-2"></span>${label}`
    : label;
}