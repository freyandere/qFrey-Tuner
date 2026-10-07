import { expect, test } from 'vitest';
import { ratePath } from '../src/components/LiveChart';
test('unknown points break the line and valid zero remains plotted', () => {
  const path = ratePath([{ at: 0, download: 0, upload: null }, { at: 1, download: null, upload: null },
    { at: 2, download: 10, upload: null }], 'download', 10);
  expect(path).toBe('M0,120  M600,10');
  expect(path).not.toContain('L');
});
