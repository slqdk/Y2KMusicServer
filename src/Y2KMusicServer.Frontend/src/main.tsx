import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter, Routes, Route } from 'react-router-dom'
import App from './App'
import Admin from './Admin'
import DjAdmin from './DjAdmin'
import './styles.css'

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <BrowserRouter>
      <Routes>
        <Route path="/"      element={<App />} />
        <Route path="/admin" element={<Admin />} />
        {/* Mobile DJ console. Case-insensitive so a typed /djadmin works too. */}
        <Route path="/DJAdmin" element={<DjAdmin />} />
        <Route path="/djadmin" element={<DjAdmin />} />
      </Routes>
    </BrowserRouter>
  </React.StrictMode>
)
