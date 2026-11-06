/**
 * Coordinate system conversions between:
 * - Physical virtual-screen space (used by Win32 / UIA / SendInput, supports negative coordinates on multi-monitor setups and Per-Monitor V2 DPI)
 * - Downscaled screenshot space (returned to the AI model by `screenshot`)
 * - Logical DPI-scaled space (100%, 125%, 150%, 175%, 200%)
 */

export type CoordSpace = 'screenshot' | 'screen';

export interface Point {
  x: number;
  y: number;
}

export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface ScreenshotMeta {
  /** Top-left origin of the captured region in physical virtual-screen coordinates (can be negative). */
  origin: Point;
  /** Ratio: imageWidth / physicalWidth (e.g. 0.5 when a 2560px screen is downscaled to 1280px). */
  scale: number;
  /** Monitor index or identifier if a specific monitor was captured. */
  monitor?: number | string;
  /** Downscaled image width in pixels. */
  width: number;
  /** Downscaled image height in pixels. */
  height: number;
  /** Original physical capture width in pixels. */
  physicalWidth: number;
  /** Original physical capture height in pixels. */
  physicalHeight: number;
  /** Timestamp when the screenshot was captured. */
  timestamp?: number;
}

/**
 * Computes the downscale factor and output dimensions given a physical capture size and maxWidth.
 * If maxWidth <= 0 or physicalWidth <= maxWidth, scale is 1.
 */
export function computeScreenshotScale(
  physicalWidth: number,
  physicalHeight: number,
  maxWidth: number = 1280,
): { scale: number; width: number; height: number } {
  if (physicalWidth <= 0 || physicalHeight <= 0) {
    throw new Error(`Invalid capture dimensions: ${physicalWidth}x${physicalHeight}`);
  }
  if (maxWidth <= 0 || physicalWidth <= maxWidth) {
    return { scale: 1, width: Math.round(physicalWidth), height: Math.round(physicalHeight) };
  }
  const scale = maxWidth / physicalWidth;
  return {
    scale,
    width: Math.round(physicalWidth * scale),
    height: Math.round(physicalHeight * scale),
  };
}

/**
 * Converts a point from downscaled screenshot space into physical virtual-screen coordinates.
 * Formula: screenX = origin.x + x / scale
 */
export function screenshotToScreen(point: Point, meta: Pick<ScreenshotMeta, 'origin' | 'scale'>): Point {
  if (!meta || !(meta.scale > 0)) {
    throw new Error('Invalid screenshot metadata: scale must be > 0');
  }
  return {
    x: Math.round(meta.origin.x + point.x / meta.scale),
    y: Math.round(meta.origin.y + point.y / meta.scale),
  };
}

/**
 * Converts a point from physical virtual-screen coordinates into downscaled screenshot space.
 * Formula: screenshotX = (screenX - origin.x) * scale
 */
export function screenToScreenshot(point: Point, meta: Pick<ScreenshotMeta, 'origin' | 'scale'>): Point {
  if (!meta || !(meta.scale > 0)) {
    throw new Error('Invalid screenshot metadata: scale must be > 0');
  }
  return {
    x: Math.round((point.x - meta.origin.x) * meta.scale),
    y: Math.round((point.y - meta.origin.y) * meta.scale),
  };
}

/**
 * Resolves an input coordinate into physical virtual-screen space according to `space`.
 * Defaults to `screenshot` space if `lastMeta` is present, otherwise falls back to `screen` space.
 */
export function resolveCoordinate(
  point: Point,
  space: CoordSpace = 'screenshot',
  lastMeta?: Pick<ScreenshotMeta, 'origin' | 'scale'> | null,
): Point {
  if (space === 'screen' || !lastMeta) {
    return { x: Math.round(point.x), y: Math.round(point.y) };
  }
  return screenshotToScreen(point, lastMeta);
}

/**
 * Converts a logical DPI-scaled coordinate on a specific monitor to physical virtual-screen coordinate.
 * Handles 100% (1.0), 125% (1.25), 150% (1.5), 175% (1.75), 200% (2.0), and negative monitor origins.
 */
export function logicalToPhysical(
  logicalPoint: Point,
  monitorPhysicalOrigin: Point,
  dpiScale: number,
): Point {
  if (!(dpiScale > 0)) throw new Error(`Invalid DPI scale: ${dpiScale}`);
  return {
    x: Math.round(monitorPhysicalOrigin.x + logicalPoint.x * dpiScale),
    y: Math.round(monitorPhysicalOrigin.y + logicalPoint.y * dpiScale),
  };
}

/**
 * Converts a physical virtual-screen coordinate on a specific monitor to logical DPI-scaled offset from that monitor's origin.
 */
export function physicalToLogical(
  physicalPoint: Point,
  monitorPhysicalOrigin: Point,
  dpiScale: number,
): Point {
  if (!(dpiScale > 0)) throw new Error(`Invalid DPI scale: ${dpiScale}`);
  return {
    x: Math.round((physicalPoint.x - monitorPhysicalOrigin.x) / dpiScale),
    y: Math.round((physicalPoint.y - monitorPhysicalOrigin.y) / dpiScale),
  };
}

/**
 * Normalizes a physical coordinate into 0..65535 absolute virtual desktop space for Win32 SendInput
 * (MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK).
 */
export function toSendInputAbsolute(
  physicalPoint: Point,
  virtualDesktop: Rect,
): Point {
  const w = Math.max(1, virtualDesktop.width - 1);
  const h = Math.max(1, virtualDesktop.height - 1);
  return {
    x: Math.round(((physicalPoint.x - virtualDesktop.x) * 65535) / w),
    y: Math.round(((physicalPoint.y - virtualDesktop.y) * 65535) / h),
  };
}
