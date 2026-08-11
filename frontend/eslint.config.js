const globals = require("globals");

// classic <script> tags (not modules) sharing one global scope, loaded in
// this order on every page: api.js, then auth.js. globals defined by an
// earlier file need to be declared here so eslint doesn't flag their use
// in a later file as "undefined".
const sharedRules = {
  "no-undef": "error",
  "no-redeclare": "error",
  "no-const-assign": "error",
  "no-dupe-keys": "error"
};

// globals api.js defines that later scripts (auth.js, etc.) use
const apiJsGlobals = {
  auth: "readonly",
  api: "readonly",
  ApiError: "readonly",
  showAlert: "readonly",
  escapeHtml: "readonly",
  formatDate: "readonly",
  formatMoney: "readonly",
  statusBadge: "readonly",
  requireLogin: "readonly"
};

module.exports = [
  {
    // the file that defines the shared globals above - don't declare
    // them here too, or eslint flags its own definitions as redeclares
    files: ["js/api.js"],
    languageOptions: {
      ecmaVersion: "latest",
      sourceType: "script",
      globals: { ...globals.browser }
    },
    rules: {
      ...sharedRules,
      "no-unused-vars": ["warn", { varsIgnorePattern: "^(auth|api|ApiError|showAlert|escapeHtml|formatDate|formatMoney|statusBadge|requireLogin)$" }]
    }
  },
  {
    files: ["js/**/*.js"],
    ignores: ["js/api.js"],
    languageOptions: {
      ecmaVersion: "latest",
      sourceType: "script",
      globals: { ...globals.browser, ...apiJsGlobals }
    },
    rules: {
      ...sharedRules,
      "no-unused-vars": ["warn", { varsIgnorePattern: "^(init|logout)" }]
    }
  }
];
