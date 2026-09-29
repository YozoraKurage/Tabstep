using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Yozolab.Tabstep.Tests")]
// The dev container's resident Unity compiles a snippet into an assembly of exactly this
// name (.devcontainer/unity/unity-do.sh run) and loads it into the running editor. Almost
// every type here is internal, so without this line a snippet could only reach the package
// through reflection — which is a poor way to check a window's actual behaviour. The
// assembly only ever exists inside that container; nothing named TabstepSnippet ships.
[assembly: InternalsVisibleTo("TabstepSnippet")]
