import type { CSSProperties } from 'react';
import { colorFromString, initialsFromName } from '../lib/colorFromString';
import styles from './Avatar.module.css';

interface AvatarProps {
  name: string;
  size?: number;
}

export function Avatar({ name, size }: AvatarProps) {
  const style = {
    backgroundColor: colorFromString(name),
    ...(size ? { '--avatar-size': `${size}px` } : {}),
  } as CSSProperties;

  return (
    <span className={styles.avatar} style={style} title={name}>
      {initialsFromName(name)}
    </span>
  );
}
