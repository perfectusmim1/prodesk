import { describe, expect, it } from 'vitest';
import {
  computeScreenshotScale,
  logicalToPhysical,
  physicalToLogical,
  resolveCoordinate,
  screenshotToScreen,
  screenToScreenshot,
  toSendInputAbsolute,
  type ScreenshotMeta,
} from '../../packages/shared/src/coords.js';

describe('Coordinate conversions & multi-monitor DPI math', () => {
  it('computes screenshot downscale factor accurately', () => {
    expect(computeScreenshotScale(1920, 1080, 1280)).toEqual({
      scale: 1280 / 1920,
      width: 1280,
      height: 720,
    });
    expect(computeScreenshotScale(2560, 1440, 1280)).toEqual({
      scale: 0.5,
      width: 1280,
      height: 720,
    });
    expect(computeScreenshotScale(3840, 2160, 1280)).toEqual({
      scale: 1 / 3,
      width: 1280,
      height: 720,
    });
    // No downscaling when maxWidth is 0 or larger than physical width
    expect(computeScreenshotScale(1024, 768, 1280)).toEqual({
      scale: 1,
      width: 1024,
      height: 768,
    });
    expect(computeScreenshotScale(3840, 2160, 0)).toEqual({
      scale: 1,
      width: 3840,
      height: 2160,
    });
  });

  it('converts between screenshot space and physical screen space on primary monitor', () => {
    const meta: ScreenshotMeta = {
      origin: { x: 0, y: 0 },
      scale: 0.5,
      width: 1280,
      height: 720,
      physicalWidth: 2560,
      physicalHeight: 1440,
    };

    const screenPt = screenshotToScreen({ x: 640, y: 360 }, meta);
    expect(screenPt).toEqual({ x: 1280, y: 720 });

    const backToShot = screenToScreenshot(screenPt, meta);
    expect(backToShot).toEqual({ x: 640, y: 360 });
  });

  it('handles negative coordinates on secondary left/top monitors accurately', () => {
    const leftMonitorMeta: ScreenshotMeta = {
      origin: { x: -2560, y: -320 },
      scale: 0.5,
      monitor: 1,
      width: 1280,
      height: 720,
      physicalWidth: 2560,
      physicalHeight: 1440,
    };

    // Top-left of left monitor
    expect(screenshotToScreen({ x: 0, y: 0 }, leftMonitorMeta)).toEqual({ x: -2560, y: -320 });
    // Center of left monitor
    expect(screenshotToScreen({ x: 640, y: 360 }, leftMonitorMeta)).toEqual({ x: -1280, y: 400 });
    // Bottom-right of left monitor
    expect(screenshotToScreen({ x: 1280, y: 720 }, leftMonitorMeta)).toEqual({ x: 0, y: 1120 });

    // Round-trip back to screenshot space
    expect(screenToScreenshot({ x: -1280, y: 400 }, leftMonitorMeta)).toEqual({ x: 640, y: 360 });
  });

  it.each([
    { label: '100% DPI', scale: 1.0, origin: { x: 0, y: 0 }, logical: { x: 400, y: 300 }, physical: { x: 400, y: 300 } },
    { label: '125% DPI', scale: 1.25, origin: { x: 0, y: 0 }, logical: { x: 400, y: 300 }, physical: { x: 500, y: 375 } },
    { label: '150% DPI', scale: 1.5, origin: { x: 1920, y: 0 }, logical: { x: 400, y: 300 }, physical: { x: 2520, y: 450 } },
    { label: '175% DPI', scale: 1.75, origin: { x: -1920, y: -180 }, logical: { x: 200, y: 100 }, physical: { x: -1570, y: -5 } },
    { label: '200% DPI', scale: 2.0, origin: { x: 0, y: -2160 }, logical: { x: 960, y: 540 }, physical: { x: 1920, y: -1080 } },
  ])('accurately maps logical <-> physical coordinates at $label', ({ scale, origin, logical, physical }) => {
    const p = logicalToPhysical(logical, origin, scale);
    expect(p).toEqual(physical);
    const l = physicalToLogical(p, origin, scale);
    expect(l).toEqual(logical);
  });

  it('resolves coordinates according to space parameter', () => {
    const meta = { origin: { x: -1920, y: 0 }, scale: 0.5 };
    expect(resolveCoordinate({ x: 500, y: 200 }, 'screenshot', meta)).toEqual({ x: -920, y: 400 });
    expect(resolveCoordinate({ x: 500, y: 200 }, 'screen', meta)).toEqual({ x: 500, y: 200 });
    expect(resolveCoordinate({ x: 500, y: 200 }, 'screenshot', null)).toEqual({ x: 500, y: 200 });
  });

  it('normalizes physical coordinates to 0..65535 for Win32 SendInput virtual desktop', () => {
    const vDesk = { x: -1920, y: 0, width: 3840, height: 1080 };
    expect(toSendInputAbsolute({ x: -1920, y: 0 }, vDesk)).toEqual({ x: 0, y: 0 });
    expect(toSendInputAbsolute({ x: 1919, y: 1079 }, vDesk)).toEqual({ x: 65535, y: 65535 });
    const mid = toSendInputAbsolute({ x: 0, y: 540 }, vDesk);
    expect(mid.x).toBeGreaterThan(32700);
    expect(mid.x).toBeLessThan(32800);
  });
});
