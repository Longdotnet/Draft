import { BrowserRouter, Navigate, Route, Routes } from "react-router-dom";
import { AppHome } from "./pages/AppHome";
import { SplashScreen } from "./pages/SplashScreen";

function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/" element={<SplashScreen />} />
        <Route path="/app" element={<AppHome />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </BrowserRouter>
  );
}

export default App;
