module.exports = [
  {
    ignores: ["**/bin/**", "**/obj/**", "node_modules/**"]
  },
  {
    files: ["src/EmbodySense.Web/wwwroot/**/*.js", "tests/frontend/**/*.mjs"],
    languageOptions: {
      ecmaVersion: "latest",
      sourceType: "module"
    },
    rules: {
      "eqeqeq": ["error", "always", { "null": "ignore" }],
      "no-undef": "off",
      "no-unused-vars": ["error", { "args": "none", "caughtErrors": "none" }],
      "no-var": "error",
      "prefer-const": "error"
    }
  }
];
