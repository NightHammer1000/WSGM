# LoadingIndicators.Avalonia — vendoring and porting notes

Upstream: <https://github.com/moviegear/LoadingIndicators.Avalonia>
License: Unlicense (public domain). `LICENSE.md` is kept here, and the copy that
ships beside `WSGM.exe` lives in `src/WSGM/Licenses/`.

## Why this is vendored as source

WSGM previously consumed the `LoadingIndicators.Avalonia` NuGet package
(11.0.11.1, last published July 2024). That package has no build for Avalonia 12,
and the failure is not cosmetic: its **precompiled** XAML binds through
`Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindings.CompiledBindingPathBuilder`,
a type Avalonia 12 removed. The publish failure identified the missing type directly:

```
ILC: Method '[LoadingIndicators.Avalonia]CompiledAvaloniaXaml.!AvaloniaResources
     +XamlClosure_17.Build(IServiceProvider)' will always throw because:
     Failed to load type '...CompiledBindingPathBuilder' from assembly
     'Avalonia.Markup.Xaml, Version=12.1.1.0'
```

Those closures are built lazily, when an indicator is first rendered — which is
why the app still *started* fine and only the boot splash would have broken. The
boot splash is the cover that hides the desktop at sign-in, so that is the worst
possible place to discover it.

WSGM compiles the two C# files and all AXAML resources directly into its own
assembly. This compiles the styles against the Avalonia version WSGM actually
ships, removes a one-consumer project and assembly, and makes a future Avalonia
breaking change fail the WSGM build instead of the splash.

## Changes from upstream

No `.axaml` file has been modified; upstream XAML compiles unchanged under
Avalonia 12's XAML compiler. The C# source carries only the explicit system
usings required by WSGM and scoped documentation/code-style pragmas because
WSGM enforces those rules on its own production code while retaining the
vendored upstream public surface and formatting unchanged.

`src/WSGM/WSGM.csproj`:

- Links both C# files into the application compile.
- Links all AXAML under `ThirdParty/LoadingIndicators`, preserving the relative
  theme includes.
- Uses WSGM's pinned Avalonia `12.1.1` package directly.
- Keeps the Unlicense copy beside `WSGM.exe` through
  `src/WSGM/Licenses/LoadingIndicators.Avalonia-UNLICENSE.txt`.

## Re-syncing

Re-clone upstream, copy the AXAML files unchanged, and re-apply the explicit
system usings plus scoped warning pragmas to the C# files. There is deliberately
no project file in this vendored tree.
