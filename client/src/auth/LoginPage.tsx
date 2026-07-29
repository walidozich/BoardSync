import { useState, type FormEvent } from 'react';
import { useAuth } from './auth-context';
import { ApiError } from '../api/client';
import styles from './LoginPage.module.css';

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
    <main className={styles.page}>
      <div className={styles.card}>
        <h1 className={styles.title}>BoardSync</h1>
        <div role="tablist" aria-label="Auth mode" className={styles.tabs}>
          <button
            type="button"
            role="tab"
            className={styles.tab}
            aria-selected={mode === 'login'}
            disabled={mode === 'login'}
            onClick={() => setMode('login')}
          >
            Log in
          </button>
          <button
            type="button"
            role="tab"
            className={styles.tab}
            aria-selected={mode === 'register'}
            disabled={mode === 'register'}
            onClick={() => setMode('register')}
          >
            Register
          </button>
        </div>

        <form onSubmit={handleSubmit} noValidate className={styles.form}>
          <label className={styles.field}>
            Email
            <input
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              autoComplete="email"
            />
          </label>
          {fieldErrors.email && (
            <p role="alert" className={styles.error}>
              {fieldErrors.email}
            </p>
          )}

          <label className={styles.field}>
            Password
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete={mode === 'login' ? 'current-password' : 'new-password'}
            />
          </label>
          {fieldErrors.password && (
            <p role="alert" className={styles.error}>
              {fieldErrors.password}
            </p>
          )}

          {mode === 'register' && (
            <>
              <label className={styles.field}>
                Display name
                <input
                  type="text"
                  value={displayName}
                  onChange={(e) => setDisplayName(e.target.value)}
                  autoComplete="nickname"
                />
              </label>
              {fieldErrors.displayName && (
                <p role="alert" className={styles.error}>
                  {fieldErrors.displayName}
                </p>
              )}
            </>
          )}

          {formError && (
            <p role="alert" className={styles.error}>
              {formError}
            </p>
          )}

          <button type="submit" className={styles.submit} disabled={submitting}>
            {mode === 'login' ? 'Log in' : 'Register'}
          </button>
        </form>
      </div>
    </main>
  );
}
