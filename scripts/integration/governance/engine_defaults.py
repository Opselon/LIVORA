"""Built-in pattern floors shared by registry + engine (no circular imports)."""
DEFAULT_GENERATED_PATTERNS = [
    "obj/**", "bin/**", "**/*.Designer.cs", "**/*.g.cs", "**/*.g.i.cs",
    "**/*AssemblyInfo.cs", "**/*.nuget.g.props", "**/*.nuget.g.targets", "**/*.user",
    "server/**/obj/**", "server/**/bin/**",
]
