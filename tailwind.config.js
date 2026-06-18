/** @type {import('tailwindcss').Config} */
export default {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    extend: {
      boxShadow: {
        glow: "0 0 28px rgba(56, 189, 248, 0.22)",
        lift: "0 18px 45px rgba(15, 23, 42, 0.18)",
      },
    },
  },
  plugins: [],
};
