import { AuthProvider } from './auth/AuthContext';
import { useAuth } from './auth/auth-context';
import { LoginPage } from './auth/LoginPage';
import { BoardPage } from './board/BoardPage';

function Gate() {
  const { isAuthenticated } = useAuth();
  return isAuthenticated ? <BoardPage /> : <LoginPage />;
}

function App() {
  return (
    <AuthProvider>
      <Gate />
    </AuthProvider>
  );
}

export default App;
