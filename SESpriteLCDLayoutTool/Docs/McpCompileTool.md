# MCP Tool: `compile_pb_script`

Allows an LLM to compile a Space Engineers PB / LCD script through the LCD
tool's Roslyn pipeline and receive SE-aware compiler errors — without opening
the game or the GUI.

---

## How the headless mode works

```
SESpriteLCDLayoutTool.exe --compile <absolute-path-to-script.cs>
```

- Writes **one JSON line** to stdout, then exits (no window is shown).
- Exit code is always `0`; success/failure is communicated via the JSON payload.
- The tool uses the same SE type stubs, boilerplate wrapping, and line-number
  adjustment that the GUI uses — errors reference your original line numbers.

### Output schema

```jsonc
// success
{ "success": true,  "errors": null,               "scriptType": "ProgrammableBlock" }

// failure
{ "success": false, "errors": "(3,1): error CS0246: The type 'IMyTextSurface' could not be found…\n(7,5): error CS0103: …", "scriptType": "ProgrammableBlock" }
```

| Field        | Type            | Notes |
|--------------|-----------------|-------|
| `success`    | bool            | `true` = compiled clean |
| `errors`     | string \| null  | Line-adjusted `(line,col): error/warning CSxxxx: …` text; `null` on success |
| `scriptType` | string \| null  | `"ProgrammableBlock"`, `"LcdHelper"`, `"ModSurface"`, `"PulsarPlugin"`, `"TorchPlugin"` |

Errors are `\n`-separated within the string.  Each entry follows the standard
csc format so they can be parsed with a simple regex:

```
\((\d+),(\d+)\):\s*(error|warning)\s+(CS\d+):\s*(.+)
```

---

## C++ MCP server tool definition

Add this to your tool list in `mcp_binary_server`:

```jsonc
{
  "name": "compile_pb_script",
  "description": "Compile a Space Engineers Programmable Block or LCD script and return SE-aware compiler errors. Use this in a write→compile→fix loop before pasting code into the game.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "script": {
        "type": "string",
        "description": "Full source text of the script to compile."
      }
    },
    "required": ["script"]
  }
}
```

### C++ handler pseudocode

```cpp
// In your tool dispatch:
if (toolName == "compile_pb_script") {
    std::string script = params["script"];

    // Write to a temp file
    char tmpPath[MAX_PATH];
    GetTempPath(MAX_PATH, tmpDir);
    // ... write script to tmpDir\se_compile_XXXX.cs

    // Shell out to the LCD tool
    std::string exePath = "C:\\path\\to\\SESpriteLCDLayoutTool.exe";
    std::string cmd = "\"" + exePath + "\" --compile \"" + tmpPath + "\"";
    std::string jsonOutput = RunProcessCaptureStdout(cmd);  // your existing helper

    // Delete temp file
    DeleteFile(tmpPath);

    // Return the JSON line directly as the tool result content
    return MakeTextContent(jsonOutput);
}
```

`RunProcessCaptureStdout` should:
- Create the process with `STARTUPINFO` redirecting stdout
- Wait up to 30 seconds (csc.exe cold-start is ~400 ms on first call)
- Return the captured stdout string (one JSON line)

---

## LLM usage pattern

```
1. LLM writes draft script
2. LLM calls compile_pb_script(script=<draft>)
3. If success==false:
     a. Parse errors string — each line is "(row,col): error CSxxxx: message"
     b. Fix the issues in the script
     c. Go to step 2
4. If success==true: present final script to user
```

The loop typically converges in 1–3 iterations for straightforward errors
(missing types, typos) and may need more for logic errors the compiler can't
catch (those require runtime execution in the full GUI).

---

## Notes

- **Cold start**: The first compile in a session takes ~400–600 ms because
  `csc.exe` itself must start. Subsequent calls in the same OS session are
  faster due to OS file caching.
- **No execution**: `--compile` mode only compiles; it never executes user
  code. There is no sandbox concern for this path.
- **SE stubs**: The script is wrapped with lightweight SE API stubs
  (`IMyTextSurface`, `MySprite`, `Vector2`, `Color`, `Echo`, etc.). Types that
  exist in real SE but are absent from the stubs will produce `CS0246` errors —
  add missing stubs to `SETypeStubs.cs` as needed.
