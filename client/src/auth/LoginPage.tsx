import { useState, type FormEvent } from 'react';
import { useAuth } from './auth-context';
import { ApiError } from '../api/client';

const MIN_PASSWORD_LENGTH = 8;
const MAX_DISPLAY_NAME_LENGTH = 50;

function validate(
  mode: 'login' | 'register',
  email: string,
  password: string,
  displayName: string,
) {
  const errors: Record<string, string> = {};

  if (!email.includes('@')) {
    errors.email = "Email must contain '@'.";
  }
  if (password.length < MIN_PASSWORD_LENGTH) {
    errors.password = `Password must be at least ${MIN_PASSWORD_LENGTH} characters.`;
  }
  if (
    mode === 'register' &&
    (displayName.length < 1 || displayName.length > MAX_DISPLAY_NAME_LENGTH)
  ) {
    errors.displayName = `Display name must be between 1 and ${MAX_DISPLAY_NAME_LENGTH} characters.`;
  }

  return errors;
}

export function LoginPage() {
  const { login, register } = useAuth();
  const [mode, setMode] = useState<'login' | 'register'>('login');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setFormError(null);

    const errors = validate(mode, email, password, displayName);
    setFieldErrors(errors);
    if (Object.keys(errors).length > 0) {
      return;
    }

    setSubmitting(true);
    try {
      if (mode === 'login') {
        await login(email, password);
      } else {
        await register(email, password, displayName);
      }
    } catch (err: unknown) {
      setFormError(err instanceof ApiError ? err.message : 'Something went wrong.');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main>
      <h1>BoardSync</h1>
      <div role="tablist" aria-label="Auth mode">
        <button
          type="button"
          role="tab"
          aria-selected={mode === 'login'}
          disabled={mode === 'login'}
          onClick={() => setMode('login')}
        >
          Log in
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={mode === 'register'}
          disabled={mode === 'register'}
          onClick={() => setMode('register')}
        >
          Register
        </button>
      </div>

      <form onSubmit={handleSubmit} noValidate>
        <label>
          Email
          <input
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            autoComplete="email"
          />
        </label>
        {fieldErrors.email && <p role="alert">{fieldErrors.email}</p>}

        <label>
          Password
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete={mode === 'login' ? 'current-password' : 'new-password'}
          />
        </label>
        {fieldErrors.password && <p role="alert">{fieldErrors.password}</p>}

        {mode === 'register' && (
          <>
            <label>
              Display name
              <input
                type="text"
                value={displayName}
                onChange={(e) => setDisplayName(e.target.value)}
                autoComplete="nickname"
              />
            </label>
            {fieldErrors.displayName && <p role="alert">{fieldErrors.displayName}</p>}
          </>
        )}

        {formError && <p role="alert">{formError}</p>}

        <button type="submit" disabled={submitting}>
          {mode === 'login' ? 'Log in' : 'Register'}
        </button>
      </form>
    </main>
  );
}
