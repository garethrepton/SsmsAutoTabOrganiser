# Working notes for Claude Code

You are implementing the project specified in `SPEC.md`. Read it first, end to
end, before writing any code.

## Hard rules

- **Build slice by slice.** The spec has 12 numbered slices. Do not start
  slice N+1 until slice N is verified working in experimental SSMS by the
  user. After completing each slice, stop and report what to test.
- **Verify VS extensibility APIs before relying on them.** Your training data
  is thin on VS 17.x and SSMS-specific APIs. When you reach for an `IVs*`
  interface or a Community Toolkit method, check it against the loaded
  assembly version. If you can't, say so and ask.
- **Threading: every VS service call is on the UI thread.** Use
  `await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync()` before
  touching any `IVs*` or DTE service. Background work (hashing, file I/O,
  SQLite) goes on the threadpool via `Task.Run`. Get this wrong and bugs are
  silent.
- **Don't invent APIs.** If the right method doesn't exist, say so rather
  than guessing a plausible name. The user has explicitly flagged this as a
  known weak spot for AI codegen on this project.
- **No automated test of the VSIX itself.** The `tests/` project covers only
  the dependency-free pieces (parser, hashing, pruner). Don't write tests
  that require a VS host - they won't run.

## Per-slice workflow

1. Read SPEC.md for the slice you're starting.
2. Implement the minimum code that satisfies the slice's "verification"
   bullet.
3. Build the solution.
4. Tell the user exactly what to do in experimental SSMS to verify, in the
   form of numbered manual steps.
5. Wait for them to confirm before moving on.

## Things to ask the user about, not guess

- The exact installed SSMS 21 version on their machine (affects shell version
  numbers in the manifest).
- Whether they want to use `Microsoft.Data.Sqlite` or `System.Data.SQLite`
  given the .NET Framework 4.7.2 target.
- Their preferred logging library if they have one.
- Whether they want to commit the `experimental/` debug profile to source
  control.

## Style

- C# 10 where the SDK supports it; fall back to C# 7.3 for parts that must
  compile against .NET Framework 4.7.2.
- File-scoped namespaces where supported.
- `async`/`await` throughout; no `.Result` or `.Wait()`.
- Records for DTOs.
- One class per file unless the helpers are tiny and tightly coupled.

## When stuck

If a VS API isn't behaving as you expect:

1. Check `Microsoft.VisualStudio.Shell.Interop` reference docs for the actual
   signature on the version you're targeting.
2. Search for examples in the Community Toolkit source on GitHub (it's the
   most reliable, current reference for VS 17 extensibility patterns).
3. Tell the user what you tried and what isn't working. Don't paper over
   broken behaviour with unrelated changes.

## What the user expects from you

- Direct, practical recommendations with no preamble.
- Honest assessments when something is harder than it looks.
- No fabricated APIs.
- Concise progress reports between slices.
