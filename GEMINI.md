# Project

## MANDATORY OUTPUT FORMAT (violations waste tokens = waste money)

### Response Structure (EVERY response must follow this)
1. Tool calls OR code blocks. Nothing else.
2. NO explanations before code. NO summaries after code.
3. NO "Let me...", "I'll...", "Here's...", "Now I'll..." preamble.
4. NO "I've completed...", "This should...", "The changes..." postamble.
5. If asked to explain: bullet points only, max 3 bullets, max 15 words each.

### File Operations
- Before reading ANY file: check if its path+purpose is documented above.
  If documented, skip the read unless you need exact line numbers.
- Never re-read a file already shown in this conversation.
- Never read files listed in "Skip These Files" section.
- Batch all related file reads into one turn (parallel tool calls).

### Code Output
- Write the minimal diff. Don't rewrite unchanged code.
- No comments explaining what the code does (the code IS the explanation).
- No type annotations unless the project already uses them.
- No docstrings unless the project already has them.

### Tool Call Discipline
- Batch file reads: read all needed files in ONE turn (parallel tool calls).
- Don't read a file just to check one thing — grep for it instead.
- Don't run tests after every small change — batch changes, test once.
- If a command fails, fix the root cause; don't retry the same command.
- Prefer Edit over Write for existing files (smaller diffs = fewer tokens).

### Session Management
- Never switch models mid-session (invalidates prompt cache).
- Prefer /clear + new session over /compact at natural breakpoints.
- If you've made 3+ tool calls without writing code, stop exploring and start writing.

## What NOT To Do (each violation wastes ~1,500 tokens)

### Don't Explore (use the map above instead)
- Don't `ls` or `find` to discover project structure -- it's documented above
- Don't read __init__.py files -- they're usually empty or just imports
- Don't read test files to understand implementation -- read the source
- Don't grep for imports -- the dependency flow is documented above

### Don't Waste Output Tokens
- Don't summarize files after reading them
- Don't explain code changes unless asked
- Don't list what you're about to do -- just do it
- Don't show full file contents -- show only the changed lines