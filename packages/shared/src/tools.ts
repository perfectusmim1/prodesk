import { z, type ZodRawShape } from 'zod';

export interface ToolDef {
  name: string;
  title: string;
  description: string;
  shape: ZodRawShape;
  timeoutMs?: number;
}

const spaceParam = z
  .enum(['screenshot', 'screen'])
  .optional()
  .describe('Coordinate space: "screenshot" (last screenshot pixel coordinates, default) or "screen" (physical virtual-screen coordinates).');

const modeParam = z
  .enum(['auto', 'uia', 'message', 'input'])
  .optional()
  .describe('Execution tier: "auto" (default: tries background UIA -> Win32 message -> SendInput with cursor/focus restore), "uia", "message", or "input".');

const selectorShape = z
  .object({
    name: z.string().optional().describe('Element Name (substring or exact match).'),
    role: z.string().optional().describe('ControlType / role (e.g. Button, Edit, Document, MenuItem, CheckBox, ComboBox, TabItem).'),
    automationId: z.string().optional().describe('UIA AutomationId.'),
    className: z.string().optional().describe('Win32/UIA ClassName.'),
    window: z.string().optional().describe('Window wid (e.g. "w1"), title substring, or process name.'),
    exact: z.boolean().optional().describe('Require exact Name match instead of case-insensitive substring.'),
  })
  .optional()
  .describe('Semantic UIA selector when uid is not used.');

const targetParams: ZodRawShape = {
  uid: z.string().optional().describe('Stable element uid from snapshot (e.g. "w1:e5"). Preferred over coordinates.'),
  selector: selectorShape,
  text: z.string().optional().describe('Visible text to locate via UIA Name first, then Windows OCR fallback.'),
  window: z.string().optional().describe('Target window wid (e.g. "w1"), title substring, or process name.'),
  x: z.number().optional().describe('Horizontal coordinate (used when uid/selector/text is omitted).'),
  y: z.number().optional().describe('Vertical coordinate (used when uid/selector/text is omitted).'),
  space: spaceParam,
};

export const TOOLS: ToolDef[] = [
  // =========================================================================
  // 5.1 System
  // =========================================================================
  {
    name: 'status',
    title: 'Prodesk status',
    description:
      'Returns helper version, connected MCP clients, pause/kill-switch state, Auto-Approve state, monitors & DPI scales, installed Windows OCR languages, Prowser reachability, and elevation status.',
    shape: {},
  },
  {
    name: 'list_monitors',
    title: 'List display monitors',
    description: 'Lists all connected monitors with physical bounds, work area, DPI/scale factor (100%..200%+), and primary flag.',
    shape: {},
  },
  {
    name: 'get_cursor',
    title: 'Get cursor position & element',
    description: 'Returns the current physical screen cursor coordinates and the UIA element directly underneath the cursor.',
    shape: {},
  },
  {
    name: 'wait',
    title: 'Wait milliseconds',
    description: 'Pauses execution for the given duration in milliseconds (max 30000). Prefer wait_for when waiting for UI changes.',
    shape: {
      ms: z.number().int().min(0).max(30_000).describe('Duration to wait in milliseconds (0..30000).'),
    },
  },
  {
    name: 'batch',
    title: 'Run multiple tools in sequence',
    description:
      'Executes a sequence of Prodesk tool calls in a single round-trip. Saves turns for multi-step desktop workflows (e.g. launch_app -> type_text -> press_key).',
    shape: {
      steps: z
        .array(
          z.object({
            tool: z.string().describe('Tool name to execute.'),
            params: z.record(z.string(), z.unknown()).optional().describe('Parameters for the tool.'),
          }),
        )
        .min(1)
        .max(30)
        .describe('Ordered list of tool invocations.'),
      stop_on_error: z.boolean().optional().describe('Stop execution on first error (default: true).'),
    },
    timeoutMs: 120_000,
  },
  {
    name: 'computer',
    title: 'Anthropic computer-use compatible action',
    description:
      'Umbrella computer-use tool supporting standard actions: screenshot, left_click, right_click, middle_click, double_click, triple_click, mouse_move, left_click_drag, left_mouse_down, left_mouse_up, scroll, type, key, hold_key, wait, cursor_position.',
    shape: {
      action: z
        .enum([
          'screenshot',
          'left_click',
          'right_click',
          'middle_click',
          'double_click',
          'triple_click',
          'mouse_move',
          'left_click_drag',
          'left_mouse_down',
          'left_mouse_up',
          'scroll',
          'type',
          'key',
          'hold_key',
          'wait',
          'cursor_position',
        ])
        .describe('Computer action to execute.'),
      coordinate: z.array(z.number()).length(2).optional().describe('[x, y] coordinate in screenshot or screen space.'),
      start_coordinate: z.array(z.number()).length(2).optional().describe('Start [x, y] coordinate for left_click_drag.'),
      text: z.string().optional().describe('Text to type or key combination to press.'),
      scroll_direction: z.enum(['up', 'down', 'left', 'right']).optional().describe('Direction to scroll.'),
      scroll_amount: z.number().optional().describe('Number of scroll ticks (default: 3).'),
      duration: z.number().optional().describe('Duration in seconds for wait or hold_key.'),
      space: spaceParam,
    },
  },

  // =========================================================================
  // 5.2 Observation
  // =========================================================================
  {
    name: 'screenshot',
    title: 'Capture desktop/window screenshot',
    description:
      'Captures the screen, a specific monitor, a window (even in background via PrintWindow/WGC), a region, or a UIA element. Returns the downscaled image plus {origin, scale, width, height} coordinate metadata. Use annotate:true for Set-of-Mark numbered boxes matching snapshot uids.',
    shape: {
      target: z
        .enum(['screen', 'monitor', 'window', 'region', 'element'])
        .optional()
        .describe('Capture target (default: "screen" or "window" if window/uid is given).'),
      monitor: z.union([z.number(), z.string()]).optional().describe('Monitor index (0-based) or device name.'),
      window: z.string().optional().describe('Window wid (e.g. "w1"), title substring, or process name.'),
      uid: z.string().optional().describe('Element uid to capture or highlight.'),
      region: z
        .object({
          x: z.number(),
          y: z.number(),
          width: z.number().positive(),
          height: z.number().positive(),
        })
        .optional()
        .describe('Physical screen rectangle to capture when target="region".'),
      annotate: z.boolean().optional().describe('Draw Set-of-Mark numbered bounding boxes for interactive elements.'),
      format: z.enum(['jpeg', 'png', 'webp']).optional().describe('Image encoding format (default: "jpeg").'),
      quality: z.number().int().min(10).max(100).optional().describe('JPEG/WebP quality 10..100 (default: 80).'),
      max_width: z.number().int().min(0).max(7680).optional().describe('Max width in pixels (default: 1280; 0 keeps original physical resolution).'),
      save_path: z.string().optional().describe('Optional absolute file path to save the image on disk.'),
      include_cursor: z.boolean().optional().describe('Draw cursor indicator on the screenshot.'),
      restore_if_minimized: z
        .boolean()
        .optional()
        .describe('Restore minimized window without stealing focus (SW_SHOWNOACTIVATE) before capturing.'),
    },
  },
  {
    name: 'snapshot',
    title: 'UI Automation accessibility tree snapshot',
    description:
      'Captures the Windows UI Automation (UIA) tree with stable uids (e.g. [w1:e5]) for every interactive element. Always prefer snapshot + uid actions over raw pixel coordinates.',
    shape: {
      window: z.string().optional().describe('Target window wid (e.g. "w1"), title substring, or process name. Omit for all visible top-level windows.'),
      interactive_only: z.boolean().optional().describe('Include only interactive/meaningful controls and their containers (default: true).'),
      max_depth: z.number().int().min(1).max(25).optional().describe('Maximum UIA tree depth (default: 10).'),
      max_chars: z.number().int().min(1000).max(100_000).optional().describe('Maximum output characters (default: 30000).'),
      diff: z.boolean().optional().describe('Return only lines changed since the previous snapshot for this window.'),
      root_uid: z.string().optional().describe('Start traversal from a specific element uid.'),
      include_offscreen: z.boolean().optional().describe('Include offscreen elements (default: false).'),
    },
  },
  {
    name: 'find',
    title: 'Find UIA elements by selector',
    description: 'Searches the UIA tree for elements matching Name, ControlType/role, AutomationId, or ClassName and returns their uids and bounds.',
    shape: {
      window: z.string().optional().describe('Window wid, title substring, or process name.'),
      name: z.string().optional().describe('Element Name substring or exact text.'),
      role: z.string().optional().describe('ControlType name (e.g. Button, Edit, Document, MenuItem, CheckBox, ListItem).'),
      automationId: z.string().optional().describe('UIA AutomationId.'),
      className: z.string().optional().describe('UIA ClassName.'),
      exact: z.boolean().optional().describe('Require exact Name match (default: false).'),
      max_results: z.number().int().min(1).max(100).optional().describe('Maximum matching elements to return (default: 25).'),
    },
  },
  {
    name: 'element_at',
    title: 'Get UIA element at coordinates',
    description: 'Returns the UIA element at the specified coordinates along with its assigned uid, properties, and supported patterns.',
    shape: {
      x: z.number().describe('Horizontal coordinate.'),
      y: z.number().describe('Vertical coordinate.'),
      space: spaceParam,
    },
  },
  {
    name: 'get_element',
    title: 'Inspect UIA element details',
    description:
      'Returns full UIA properties (Name, AutomationId, ControlType, ClassName, Value, IsEnabled, IsOffscreen, IsPassword, BoundingRectangle) and supported patterns (Invoke, Value, Toggle, SelectionItem, ExpandCollapse, Scroll, RangeValue, LegacyIAccessible) for a uid.',
    shape: {
      uid: z.string().describe('Element uid (e.g. "w1:e5").'),
    },
  },
  {
    name: 'ocr',
    title: 'Run Windows native OCR',
    description:
      'Runs built-in Windows.Media.Ocr (tr-TR / en-US) on the screen, a monitor, a window, or a region and returns recognized lines and words with physical screen bounding boxes.',
    shape: {
      window: z.string().optional().describe('Window wid, title substring, or process name.'),
      monitor: z.union([z.number(), z.string()]).optional().describe('Monitor index.'),
      region: z
        .object({
          x: z.number(),
          y: z.number(),
          width: z.number().positive(),
          height: z.number().positive(),
        })
        .optional()
        .describe('Physical screen region to OCR.'),
      language: z.string().optional().describe('BCP-47 language tag (e.g. "tr-TR" or "en-US"). Defaults to system/Turkish.'),
    },
  },
  {
    name: 'find_text',
    title: 'Find text on screen via UIA + OCR',
    description:
      'Finds visible text inside a window or on screen by searching UIA element names/values first and falling back to Windows OCR. Returns matching coordinates and uids.',
    shape: {
      text: z.string().min(1).describe('Text to search for.'),
      exact: z.boolean().optional().describe('Require exact match (default: false, case-insensitive substring).'),
      window: z.string().optional().describe('Optional window wid, title substring, or process name.'),
      language: z.string().optional().describe('OCR language tag if OCR fallback is used.'),
    },
  },
  {
    name: 'wait_for',
    title: 'Wait for UI element, text, or window state',
    description:
      'Polls until an element, text, or window appears/disappears, or a window title matches. Enforces a hard timeout so execution never hangs.',
    shape: {
      uid: z.string().optional().describe('Element uid to wait for.'),
      selector: selectorShape,
      text: z.string().optional().describe('Visible text (UIA or OCR) to wait for.'),
      window: z.string().optional().describe('Window wid, title substring, or process name.'),
      state: z
        .enum(['appear', 'disappear', 'enabled', 'focused'])
        .optional()
        .describe('Desired condition (default: "appear").'),
      timeout_ms: z.number().int().min(100).max(30_000).optional().describe('Maximum wait time in ms (default: 10000).'),
      interval_ms: z.number().int().min(50).max(2000).optional().describe('Polling interval in ms (default: 200).'),
    },
    timeoutMs: 35_000,
  },

  // =========================================================================
  // 5.3 Input & Interaction (3-Tier Non-Disruptive Action Engine)
  // =========================================================================
  {
    name: 'click',
    title: 'Click UI element or coordinate',
    description:
      'Clicks a target using the 3-tier non-disruptive engine: Tier 1 UIA Invoke/Toggle/SelectionItem (no cursor/focus steal) -> Tier 2 background Win32 WM_LBUTTONDOWN/UP -> Tier 3 SendInput with cursor & foreground window restoration.',
    shape: {
      ...targetParams,
      button: z.enum(['left', 'right', 'middle']).optional().describe('Mouse button (default: "left").'),
      double: z.boolean().optional().describe('Double-click (default: false).'),
      modifiers: z.array(z.enum(['ctrl', 'alt', 'shift', 'win'])).optional().describe('Modifier keys held during click.'),
      mode: modeParam,
    },
  },
  {
    name: 'hover',
    title: 'Hover over element or coordinate',
    description: 'Moves pointer over the target element or coordinate.',
    shape: {
      ...targetParams,
      duration_ms: z.number().int().min(0).max(5000).optional().describe('Hold hover duration in ms.'),
    },
  },
  {
    name: 'mouse_move',
    title: 'Move mouse cursor',
    description: 'Moves the mouse cursor to the given coordinate in screenshot or screen space.',
    shape: {
      x: z.number().describe('Horizontal coordinate.'),
      y: z.number().describe('Vertical coordinate.'),
      space: spaceParam,
    },
  },
  {
    name: 'drag',
    title: 'Drag and drop between elements or coordinates',
    description: 'Drags from a source element/coordinate to a destination element/coordinate with smooth intermediate steps and restores prior cursor/focus.',
    shape: {
      from_uid: z.string().optional().describe('Source element uid.'),
      from_x: z.number().optional().describe('Source X coordinate.'),
      from_y: z.number().optional().describe('Source Y coordinate.'),
      to_uid: z.string().optional().describe('Destination element uid.'),
      to_x: z.number().optional().describe('Destination X coordinate.'),
      to_y: z.number().optional().describe('Destination Y coordinate.'),
      duration_ms: z.number().int().min(50).max(5000).optional().describe('Drag duration in ms (default: 350).'),
      space: spaceParam,
    },
  },
  {
    name: 'scroll',
    title: 'Scroll element or window',
    description: 'Scrolls a target element or window using UIA ScrollPattern first, falling back to WM_MOUSEWHEEL / SendInput wheel events.',
    shape: {
      ...targetParams,
      dx: z.number().optional().describe('Horizontal scroll ticks (+right, -left).'),
      dy: z.number().optional().describe('Vertical scroll ticks (+down, -up; default: 3).'),
      to: z.enum(['top', 'bottom', 'left', 'right']).optional().describe('Scroll directly to an edge via UIA ScrollPattern.'),
      mode: modeParam,
    },
  },
  {
    name: 'type_text',
    title: 'Type text (background-first, full Turkish Unicode support)',
    description:
      'Enters text into a target control. Uses UIA ValuePattern in background when possible, otherwise WM_CHAR or KEYEVENTF_UNICODE (layout-independent, supports Turkish İ ı Ş ş Ğ ğ Ü ü Ö ö Ç ç). Restores clipboard if via_clipboard is used.',
    shape: {
      ...targetParams,
      text: z.string().describe('Text to enter.'),
      clear_first: z.boolean().optional().describe('Clear existing value before typing (default: false).'),
      submit: z.boolean().optional().describe('Press Enter after typing (default: false).'),
      via_clipboard: z.boolean().optional().describe('Paste via clipboard while backing up and restoring the user clipboard.'),
      mode: modeParam,
    },
  },
  {
    name: 'press_key',
    title: 'Press key or shortcut',
    description:
      'Sends a key or shortcut combination (e.g. "ctrl+s", "alt+f4", "enter", "ctrl+shift+n") to a target window or focused control. Releases all modifiers in a finally block so keys never stick.',
    shape: {
      keys: z.union([z.string(), z.array(z.string())]).describe('Key chord (e.g. "ctrl+s") or sequence of key chords.'),
      window: z.string().optional().describe('Target window wid, title substring, or process name.'),
      uid: z.string().optional().describe('Optional target element uid.'),
      repeat: z.number().int().min(1).max(50).optional().describe('Repeat count (default: 1).'),
      mode: modeParam,
    },
  },
  {
    name: 'set_value',
    title: 'Set UIA control value directly',
    description: 'Sets the value of an Edit, Document, ComboBox, Slider, or Spinner directly via UIA ValuePattern or RangeValuePattern without stealing focus.',
    shape: {
      ...targetParams,
      value: z.union([z.string(), z.number()]).describe('Value to assign.'),
    },
  },
  {
    name: 'invoke',
    title: 'Invoke UIA control',
    description: 'Triggers UIA InvokePattern.Invoke() or LegacyIAccessible.DoDefaultAction() on a button, menu item, or link strictly in the background.',
    shape: {
      ...targetParams,
    },
  },
  {
    name: 'toggle',
    title: 'Toggle checkbox or switch',
    description: 'Toggles a CheckBox, ToggleButton, or Switch via UIA TogglePattern without moving the cursor.',
    shape: {
      ...targetParams,
      state: z.enum(['on', 'off', 'toggle']).optional().describe('Desired state (default: "toggle").'),
    },
  },
  {
    name: 'select_option',
    title: 'Select option in ComboBox, List, or Tab',
    description: 'Selects an item inside a ComboBox, ListBox, TreeView, or TabControl by option text or 0-based index using UIA SelectionItemPattern.',
    shape: {
      ...targetParams,
      option: z.string().optional().describe('Visible text of the option to select.'),
      index: z.number().int().min(0).optional().describe('0-based index of the option to select.'),
    },
  },
  {
    name: 'expand',
    title: 'Expand menu, dropdown, or tree node',
    description: 'Expands an element supporting UIA ExpandCollapsePattern.',
    shape: {
      ...targetParams,
    },
  },
  {
    name: 'collapse',
    title: 'Collapse menu, dropdown, or tree node',
    description: 'Collapses an element supporting UIA ExpandCollapsePattern.',
    shape: {
      ...targetParams,
    },
  },
  {
    name: 'focus',
    title: 'Focus UIA element',
    description: 'Sets keyboard focus on a specific UIA element.',
    shape: {
      ...targetParams,
    },
  },

  // =========================================================================
  // 5.4 Window & Application Management
  // =========================================================================
  {
    name: 'list_windows',
    title: 'List open desktop windows',
    description:
      'Lists all open top-level windows with wid ("w1"..), HWND, title, process name, pid, class name, bounds, monitor, state (normal/minimized/maximized), foreground flag, and elevation status.',
    shape: {
      include_minimized: z.boolean().optional().describe('Include minimized windows (default: true).'),
      filter: z.string().optional().describe('Optional substring filter on title or process name.'),
    },
  },
  {
    name: 'focus_window',
    title: 'Bring window to foreground',
    description:
      'Brings the target window to the foreground. Rarely needed — prefer background UIA actions so the user is not interrupted.',
    shape: {
      window: z.string().describe('Window wid (e.g. "w1"), title substring, or process name.'),
    },
  },
  {
    name: 'window_action',
    title: 'Move, resize, minimize, maximize, restore, or close window',
    description:
      'Performs a window management action: minimize, maximize, restore, restore_noactivate, close, move, resize, topmost, untopmost.',
    shape: {
      window: z.string().describe('Window wid (e.g. "w1"), title substring, or process name.'),
      action: z
        .enum(['minimize', 'maximize', 'restore', 'restore_noactivate', 'close', 'move', 'resize', 'topmost', 'untopmost'])
        .describe('Window operation to execute.'),
      x: z.number().int().optional().describe('New X coordinate (for move).'),
      y: z.number().int().optional().describe('New Y coordinate (for move).'),
      width: z.number().int().positive().optional().describe('New width in pixels (for resize/move).'),
      height: z.number().int().positive().optional().describe('New height in pixels (for resize/move).'),
    },
  },
  {
    name: 'launch_app',
    title: 'Launch desktop or UWP application',
    description:
      'Launches an application by executable path, friendly Start-Menu/registered name (e.g. "notepad", "mspaint", "calc"), or UWP AUMID (e.g. "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"). Defaults to background:true (SW_SHOWNOACTIVATE) so the user keeps focus.',
    shape: {
      path: z.string().optional().describe('Executable path or command name (e.g. "notepad.exe").'),
      name: z.string().optional().describe('Friendly application name from Start Menu or registered apps.'),
      aumid: z.string().optional().describe('UWP Application User Model ID (e.g. "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App").'),
      args: z.array(z.string()).optional().describe('Command-line arguments.'),
      cwd: z.string().optional().describe('Working directory.'),
      background: z.boolean().optional().describe('Start without stealing foreground focus (default: true).'),
      wait_for_window: z.boolean().optional().describe('Wait up to timeout for main window to appear and return its wid (default: true).'),
      timeout_ms: z.number().int().min(500).max(30_000).optional().describe('Timeout waiting for window (default: 8000).'),
    },
  },
  {
    name: 'close_app',
    title: 'Close application gracefully or forcefully',
    description: 'Closes an application by sending WM_CLOSE to its windows first, or terminates it if force:true.',
    shape: {
      window: z.string().optional().describe('Window wid or title substring.'),
      process: z.string().optional().describe('Process name (e.g. "notepad.exe").'),
      pid: z.number().int().positive().optional().describe('Process ID.'),
      force: z.boolean().optional().describe('Force kill process if graceful close does not exit (default: false).'),
    },
  },
  {
    name: 'list_apps',
    title: 'List installed desktop & UWP apps',
    description: 'Lists installed applications discovered from Start Menu shortcuts, Registry Uninstall keys, and UWP AppX packages (with AUMIDs).',
    shape: {
      filter: z.string().optional().describe('Optional case-insensitive name filter.'),
    },
  },
  {
    name: 'open_path',
    title: 'Open file, folder, or URL with default handler',
    description: 'Opens a file or folder with its default Windows application via ShellExecute. For web URLs, prefer the Prowser browser tool.',
    shape: {
      target: z.string().describe('File path, folder path, or URI to open.'),
    },
  },

  // =========================================================================
  // 5.5 File System
  // =========================================================================
  {
    name: 'fs_list',
    title: 'List directory contents',
    description: 'Lists files and folders inside a directory with size, modified timestamp, and attributes.',
    shape: {
      path: z.string().describe('Directory path (supports %USERPROFILE%, %TEMP%, etc.).'),
      pattern: z.string().optional().describe('Optional glob/wildcard filter (e.g. "*.txt").'),
      recursive: z.boolean().optional().describe('Recurse into subdirectories (default: false).'),
      max_items: z.number().int().min(1).max(2000).optional().describe('Maximum entries to return (default: 250).'),
    },
  },
  {
    name: 'fs_info',
    title: 'Get file or directory metadata',
    description: 'Returns detailed metadata for a file or folder: size, creation/modification time, attributes, SHA256 hash (for files <= 100 MB), and real resolved target.',
    shape: {
      path: z.string().describe('Path to inspect.'),
    },
  },
  {
    name: 'fs_read',
    title: 'Read text or binary file',
    description: 'Reads a file with automatic encoding detection (UTF-8, UTF-16LE/BE, Windows-1254 Turkish) or returns base64 for binary files.',
    shape: {
      path: z.string().describe('File path to read.'),
      encoding: z.enum(['auto', 'utf8', 'utf16le', 'windows1254', 'base64']).optional().describe('Encoding mode (default: "auto").'),
      max_bytes: z.number().int().min(1).max(10_485_760).optional().describe('Maximum bytes to read (default: 524288).'),
    },
  },
  {
    name: 'fs_write',
    title: 'Write or append file atomically',
    description: 'Writes content to a file atomically (via temporary file + rename) or appends to an existing file.',
    shape: {
      path: z.string().describe('Destination file path.'),
      content: z.string().describe('Text content or base64 payload.'),
      encoding: z.enum(['utf8', 'base64']).optional().describe('Content encoding (default: "utf8").'),
      append: z.boolean().optional().describe('Append to file instead of atomic overwrite (default: false).'),
    },
  },
  {
    name: 'fs_copy',
    title: 'Copy file or directory',
    description: 'Copies a file or directory tree to a destination path.',
    shape: {
      source: z.string().describe('Source file or directory path.'),
      destination: z.string().describe('Destination path.'),
      overwrite: z.boolean().optional().describe('Overwrite existing destination (default: true).'),
    },
  },
  {
    name: 'fs_move',
    title: 'Move or rename file or directory',
    description: 'Moves or renames a file or directory.',
    shape: {
      source: z.string().describe('Source path.'),
      destination: z.string().describe('Destination path.'),
      overwrite: z.boolean().optional().describe('Overwrite existing destination (default: false).'),
    },
  },
  {
    name: 'fs_mkdir',
    title: 'Create directory',
    description: 'Creates a directory and all missing parent directories.',
    shape: {
      path: z.string().describe('Directory path to create.'),
    },
  },
  {
    name: 'fs_delete',
    title: 'Delete file or folder (Recycle Bin by default)',
    description:
      'Deletes a file or directory. By default sends items safely to the Windows Recycle Bin (FOF_ALLOWUNDO). Setting permanent:true requires critical confirmation.',
    shape: {
      path: z.string().describe('File or directory path to delete.'),
      permanent: z.boolean().optional().describe('Permanently delete instead of sending to Recycle Bin (default: false).'),
    },
  },
  {
    name: 'fs_search',
    title: 'Search files by name or content',
    description: 'Searches a directory tree for files matching a filename pattern and/or text content.',
    shape: {
      path: z.string().describe('Root directory to search.'),
      name_pattern: z.string().optional().describe('Filename glob or substring (e.g. "*.pdf" or "rapor").'),
      content_pattern: z.string().optional().describe('Case-insensitive substring or regex to search inside text files.'),
      max_results: z.number().int().min(1).max(500).optional().describe('Max results to return (default: 100).'),
    },
  },
  {
    name: 'reveal_in_explorer',
    title: 'Reveal file or folder in Explorer',
    description: 'Opens Windows File Explorer with the specified file or folder selected (`explorer.exe /select,path`).',
    shape: {
      path: z.string().describe('Path to reveal.'),
    },
  },

  // =========================================================================
  // 5.6 Downloads & Package Installation
  // =========================================================================
  {
    name: 'download_file',
    title: 'Download file over HTTPS with SHA256 verification',
    description:
      'Downloads a file from an http/https URL (blocks file:// and private SSRF IPs by default) to %USERPROFILE%\\Downloads or a custom path, computes its SHA256 hash, and optionally verifies an expected SHA256.',
    shape: {
      url: z.string().url().describe('HTTP/HTTPS URL to download.'),
      destination: z.string().optional().describe('Optional destination file or directory path (defaults to %USERPROFILE%\\Downloads).'),
      filename: z.string().optional().describe('Optional filename override.'),
      expected_sha256: z.string().optional().describe('Optional expected SHA256 hex digest to verify.'),
      max_bytes: z.number().int().positive().optional().describe('Maximum download size in bytes (default: 1073741824 = 1 GB).'),
    },
    timeoutMs: 180_000,
  },
  {
    name: 'github_release',
    title: 'Download asset from GitHub Release',
    description:
      'Queries GitHub Releases for owner/repo (latest or specific tag), matches a release asset by glob/substring (e.g. "*win64*.exe" or "*amd64*.msi"), and downloads it with SHA256 digest.',
    shape: {
      repo: z.string().describe('GitHub repository in "owner/repo" format (e.g. "jqlang/jq").'),
      tag: z.string().optional().describe('Release tag (default: "latest").'),
      asset_pattern: z.string().optional().describe('Glob or substring pattern to select the release asset (e.g. "*win64*.exe").'),
      download: z.boolean().optional().describe('Download the matched asset to disk (default: true; false only lists assets).'),
      destination: z.string().optional().describe('Optional destination folder (defaults to %USERPROFILE%\\Downloads).'),
    },
    timeoutMs: 180_000,
  },
  {
    name: 'winget_search',
    title: 'Search packages via winget',
    description: 'Searches Windows Package Manager (winget) for software packages matching a query.',
    shape: {
      query: z.string().min(1).describe('Package name or keyword to search.'),
      max_results: z.number().int().min(1).max(50).optional().describe('Maximum packages to return (default: 20).'),
    },
    timeoutMs: 60_000,
  },
  {
    name: 'winget_list',
    title: 'List installed winget packages',
    description: 'Lists installed packages reported by winget, optionally filtered by query.',
    shape: {
      query: z.string().optional().describe('Optional filter query.'),
    },
    timeoutMs: 60_000,
  },
  {
    name: 'winget_install',
    title: 'Install package via winget',
    description:
      'Installs a package silently using winget (`--exact --silent --accept-package-agreements --accept-source-agreements`).',
    shape: {
      id: z.string().min(1).describe('Exact winget package ID (e.g. "7zip.7zip").'),
      version: z.string().optional().describe('Optional specific version.'),
      scope: z.enum(['user', 'machine']).optional().describe('Installation scope (prefer "user" to avoid UAC).'),
    },
    timeoutMs: 300_000,
  },
  {
    name: 'winget_uninstall',
    title: 'Uninstall package via winget',
    description: 'Uninstalls a package silently via winget.',
    shape: {
      id: z.string().min(1).describe('Exact winget package ID.'),
    },
    timeoutMs: 180_000,
  },
  {
    name: 'verify_file',
    title: 'Verify Authenticode digital signature & SHA256',
    description:
      'Verifies the Windows Authenticode signature (status, signer subject, issuer, thumbprint) and SHA256 hash of an executable or installer file.',
    shape: {
      path: z.string().describe('Path to the file (.exe, .msi, .msix, .dll) to verify.'),
    },
  },
  {
    name: 'run_installer',
    title: 'Verify and run software installer',
    description:
      'Verifies the Authenticode signature of an installer (.exe, .msi, .msix) first, then executes it (silently by default). Unsigned installers require explicit critical confirmation.',
    shape: {
      path: z.string().describe('Path to the installer file.'),
      args: z.array(z.string()).optional().describe('Custom installer arguments (defaults to silent flags for MSI/NSIS/Inno).'),
      silent: z.boolean().optional().describe('Use silent installation flags when args are not provided (default: true).'),
      timeout_ms: z.number().int().min(1000).max(600_000).optional().describe('Timeout in ms (default: 180000).'),
    },
    timeoutMs: 300_000,
  },

  // =========================================================================
  // 5.7 Clipboard
  // =========================================================================
  {
    name: 'clipboard_read',
    title: 'Read Windows clipboard',
    description: 'Reads the current clipboard content in the requested format: text, html, rtf, image (returned as PNG), or file drop list.',
    shape: {
      format: z.enum(['text', 'html', 'rtf', 'image', 'files']).optional().describe('Clipboard format to read (default: "text").'),
    },
  },
  {
    name: 'clipboard_write',
    title: 'Write to Windows clipboard',
    description: 'Writes text, HTML, RTF, image (from base64 or file path), or file drop paths to the Windows clipboard.',
    shape: {
      format: z.enum(['text', 'html', 'rtf', 'image', 'files']).optional().describe('Clipboard format (default: "text").'),
      text: z.string().optional().describe('Text, HTML, or RTF string, or base64 PNG data when format="image".'),
      files: z.array(z.string()).optional().describe('File paths when format="files".'),
    },
  },
  {
    name: 'clipboard_history',
    title: 'Read Windows clipboard history & ring buffer',
    description:
      'Returns recent clipboard entries from Windows Clipboard History (Win+V API) and/or Prodesk Helper in-memory ring buffer (excluding password-manager excluded items).',
    shape: {
      max_items: z.number().int().min(1).max(100).optional().describe('Maximum items to return (default: 25).'),
    },
  },
  {
    name: 'clipboard_clear',
    title: 'Clear Windows clipboard',
    description: 'Clears the current Windows clipboard.',
    shape: {},
  },

  // =========================================================================
  // 5.8 Shell & Processes
  // =========================================================================
  {
    name: 'run_command',
    title: 'Execute shell command (UTF-8 enforced)',
    description:
      'Executes a command in PowerShell, pwsh, or cmd with UTF-8 output encoding enforced (preserving Turkish characters). Supports foreground execution with hard timeout or background job tracking.',
    shape: {
      command: z.string().min(1).describe('Command string to execute.'),
      shell: z.enum(['powershell', 'pwsh', 'cmd']).optional().describe('Shell interpreter (default: "powershell").'),
      cwd: z.string().optional().describe('Working directory.'),
      env: z.record(z.string(), z.string()).optional().describe('Additional environment variables.'),
      timeout_ms: z.number().int().min(500).max(300_000).optional().describe('Timeout in ms for foreground execution (default: 60000).'),
      background: z.boolean().optional().describe('Run asynchronously in background and return a job_id (default: false).'),
    },
    timeoutMs: 310_000,
  },
  {
    name: 'command_status',
    title: 'Check background command status & output',
    description: 'Returns the status (running/exited), exit code, and recent stdout/stderr of a background command started with run_command({background:true}).',
    shape: {
      job_id: z.string().describe('Background job ID returned by run_command.'),
    },
  },
  {
    name: 'command_input',
    title: 'Send stdin to background command',
    description: 'Writes input text to the standard input stream of a running background command.',
    shape: {
      job_id: z.string().describe('Background job ID.'),
      input: z.string().describe('Text to write to stdin (include "\\n" for newline).'),
    },
  },
  {
    name: 'command_kill',
    title: 'Terminate background command',
    description: 'Terminates a running background command and its process tree.',
    shape: {
      job_id: z.string().describe('Background job ID to terminate.'),
    },
  },
  {
    name: 'list_processes',
    title: 'List running processes',
    description: 'Lists running processes with PID, name, memory usage (MB), CPU time, main window title, executable path, and elevation state.',
    shape: {
      filter: z.string().optional().describe('Optional name or title substring filter.'),
      max_results: z.number().int().min(1).max(500).optional().describe('Maximum processes to return (default: 100).'),
    },
  },
  {
    name: 'kill_process',
    title: 'Terminate process by PID or name',
    description: 'Terminates a process (and optionally its child tree) by PID or process name.',
    shape: {
      pid: z.number().int().positive().optional().describe('Process ID to terminate.'),
      name: z.string().optional().describe('Process name to terminate (e.g. "notepad.exe").'),
      tree: z.boolean().optional().describe('Kill child process tree as well (default: true).'),
    },
  },
  {
    name: 'system_info',
    title: 'Get Windows hardware & OS information',
    description: 'Returns OS version/build, CPU model & cores, total/free RAM, disks with free space, GPUs, battery status, network adapters, uptime, and display scaling.',
    shape: {},
  },
  {
    name: 'notify',
    title: 'Show Windows desktop notification',
    description: 'Displays a non-intrusive Windows balloon/toast notification to the user.',
    shape: {
      title: z.string().min(1).describe('Notification title.'),
      message: z.string().min(1).describe('Notification body text.'),
      level: z.enum(['info', 'warning', 'error']).optional().describe('Notification icon level (default: "info").'),
    },
  },

  // =========================================================================
  // 5.9 Screen Recording, Session Report & Macros
  // =========================================================================
  {
    name: 'record_start',
    title: 'Start MP4/frame screen recording',
    description: 'Starts recording the screen, a monitor, a window, or a region in the background.',
    shape: {
      target: z.enum(['screen', 'monitor', 'window', 'region']).optional().describe('Recording target (default: "screen").'),
      window: z.string().optional().describe('Window wid or title when target="window".'),
      monitor: z.number().int().min(0).optional().describe('Monitor index when target="monitor".'),
      region: z
        .object({
          x: z.number(),
          y: z.number(),
          width: z.number().positive(),
          height: z.number().positive(),
        })
        .optional()
        .describe('Region bounds when target="region".'),
      fps: z.number().int().min(1).max(30).optional().describe('Frames per second (default: 10).'),
      max_seconds: z.number().int().min(1).max(300).optional().describe('Maximum recording duration in seconds (default: 120).'),
      include_cursor: z.boolean().optional().describe('Include mouse cursor in recording (default: true).'),
    },
  },
  {
    name: 'record_stop',
    title: 'Stop screen recording and save MP4',
    description: 'Stops the active recording, encodes it as an H.264 MP4 video file, and returns the file path, duration, and size.',
    shape: {
      save_path: z.string().optional().describe('Optional custom output .mp4 path.'),
    },
    timeoutMs: 60_000,
  },
  {
    name: 'record_to_gif',
    title: 'Export recent recording or capture clip as GIF',
    description: 'Captures a short clip or converts the last recording into an optimized animated GIF.',
    shape: {
      seconds: z.number().min(1).max(15).optional().describe('Duration to capture in seconds if not converting an existing recording (default: 3).'),
      fps: z.number().int().min(2).max(15).optional().describe('GIF frames per second (default: 8).'),
      max_width: z.number().int().min(200).max(1280).optional().describe('Max GIF width in pixels (default: 640).'),
      save_path: z.string().optional().describe('Optional custom output .gif path.'),
      window: z.string().optional().describe('Optional target window.'),
    },
    timeoutMs: 60_000,
  },
  {
    name: 'session_report',
    title: 'Generate HTML action timeline report',
    description: 'Generates a visual HTML timeline of all actions executed in the current session with before/after thumbnails and verification results.',
    shape: {
      save_path: z.string().optional().describe('Optional output .html path.'),
    },
  },
  {
    name: 'macro_record_start',
    title: 'Start recording semantic macro',
    description: 'Starts recording subsequent Prodesk tool actions into a named semantic macro (storing UIA selectors/AutomationIds so replay works even if windows move).',
    shape: {
      name: z.string().min(1).describe('Macro name (e.g. "notepad-save-note").'),
      description: z.string().optional().describe('Optional description of the macro.'),
    },
  },
  {
    name: 'macro_record_stop',
    title: 'Stop macro recording and save',
    description: 'Stops macro recording and persists the macro JSON to %LOCALAPPDATA%\\Prodesk\\macros\\.',
    shape: {},
  },
  {
    name: 'macro_run',
    title: 'Replay saved semantic macro',
    description: 'Replays a saved macro by re-resolving UIA selectors/window targets dynamically rather than relying on fixed screen coordinates.',
    shape: {
      name: z.string().min(1).describe('Name of the macro to run.'),
      variables: z.record(z.string(), z.string()).optional().describe('Optional template variables to substitute (e.g. {"{{text}}": "Hello"}).'),
      stop_on_error: z.boolean().optional().describe('Stop replay on first step error (default: true).'),
    },
    timeoutMs: 120_000,
  },
  {
    name: 'macro_list',
    title: 'List saved macros',
    description: 'Lists all saved semantic macros in %LOCALAPPDATA%\\Prodesk\\macros\\.',
    shape: {},
  },
  {
    name: 'macro_delete',
    title: 'Delete saved macro',
    description: 'Deletes a saved macro by name.',
    shape: {
      name: z.string().min(1).describe('Macro name to delete.'),
    },
  },

  // =========================================================================
  // 5.10 Prowser Integration
  // =========================================================================
  {
    name: 'browser',
    title: 'Proxy browser action to Prowser MCP hub',
    description:
      'Connects as a peer to the local Prowser hub (ws://127.0.0.1:17890..17899/peer using %LOCALAPPDATA%\\Prowser\\hub.token) and invokes any Prowser tool. Always use Prowser (with new_window) for web page DOM interaction instead of desktop OCR/coordinates.',
    shape: {
      tool: z.string().min(1).describe('Prowser tool name (e.g. "prowser_status", "new_window", "snapshot", "click", "type_text", "close_window").'),
      params: z.record(z.string(), z.unknown()).optional().describe('Parameters to pass to the Prowser tool.'),
    },
    timeoutMs: 60_000,
  },
];

export const TOOL_MAP = new Map<string, ToolDef>(TOOLS.map((t) => [t.name, t]));
