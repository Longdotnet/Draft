import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import "./styles.css";

const canonicalAppBaseUrl = (import.meta.env.VITE_CANONICAL_APP_BASE_URL ?? "").trim();
let redirectingToCanonical = false;

if (canonicalAppBaseUrl) {
  try {
    const canonicalUrl = new URL(canonicalAppBaseUrl);
    if (canonicalUrl.origin !== window.location.origin) {
      const targetUrl = new URL(
        `${window.location.pathname}${window.location.search}${window.location.hash}`,
        canonicalUrl.origin,
      );
      window.location.replace(targetUrl.toString());
      redirectingToCanonical = true;
    }
  } catch {
    // Ignore an invalid optional canonical URL and render normally.
  }
}

if (!redirectingToCanonical) {
  ReactDOM.createRoot(document.getElementById("root")!).render(
    <React.StrictMode>
      <App />
    </React.StrictMode>,
  );
}
