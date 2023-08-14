From public copy at https://gist.github.com/jimklimov/1f5f5e8b20398e70852bda6f4f0c667b

Example below stems from my adventure starting with C#/.NET by fixing some issues in CycloneDX tooling.
One big caveat was getting my custom-built library used by the custom-built tool (and VS Code IDE to debug).
Ended up with the following; maybe better ways exist...

* Install DotNet and NuGet
  * TODO: How did I get that on Windows?
  * Ubuntu Linux (in WSL):
````
:; sudo apt-get update && sudo apt-get install dotnet7 nuget
````

* Check out the sources (in `HOME` for the example)
````
:; cd ~
:; git clone https://github.com/CycloneDX/cyclonedx-dotnet-library
:; git clone https://github.com/CycloneDX/cyclonedx-cli
````

* Created a local NuGet repo location to replace private library builds in, using a uniquely suffixed version:
````
:; cd ~/cyclonedx-dotnet-library
:; echo > _Cfg <<EOF
#!/bin/sh

[ -d ~/.nuget ] || nuget sources Add -Name userhome -Source ~/.nuget

LIBVER="`cat semver.txt`.1" || LIBVER="5.4.0.1"

for P in src/*/*.csproj ; do
    sed -e 's,<TargetFramework>netstandard2.0</TargetFramework>,<TargetFramework>net6.0</TargetFramework>,' -i "$P"
done

dotnet build \
&& dotnet pack -p:Version="${LIBVER}" -p:TargetFrameworks=net6.0 -p:TargetFramework=net6.0 \
&& for P in `pwd`/src/*/bin/Debug/*"${LIBVER}"*.nupkg ; do \
    nuget delete -NonInteractive `basename "$P" ".${LIBVER}.nupkg"` "${LIBVER}" -Source ~/.nuget

    # Unlike "nuget add" (missing in Linux), this one uses the
    # path arg as relative even if it is absolute by wording:
    (cd / && nuget push "$P" -Source ~/.nuget)
done
EOF
````
  * As seen above, also had to use `net6.0` target instead of `netstandard2.0`;
    internet lore says `netstandard2.1` may also work - primary goal here is
    to have newer language features by default (codebase is apparently C# 9.0+
    but does not say so in manifests).
  * On Windows (with Git Bash), `nuget add "$P" -Source ~/.nuget' without a `cd`,
    uses the absolute path as such.

* For the tool using the library, gotta patch its recipe to consult the private
  local repo, and use custom versions, according to the patch-format blob below:
````
--- a/src/cyclonedx/cyclonedx.csproj
+++ b/src/cyclonedx/cyclonedx.csproj
@@ -10,13 +10,16 @@
 
   <PropertyGroup>
     <Description>A command line tool for interacting with CycloneDX bill-of-material documents.</Description>
+    <RestoreSources>C:\\Users\\jim\\nuget;$(RestoreSources);https://api.nuget.org/v3/index.json</RestoreSources>^M
   </PropertyGroup>
 
   <ItemGroup>
     <PackageReference Include="CoderPatros.AntPathMatching" Version="0.1.1" />
     <PackageReference Include="CsvHelper" Version="29.0.0" />
-    <PackageReference Include="CycloneDX.Utils" Version="5.4.0" />
-    <PackageReference Include="CycloneDX.Spdx.Interop" Version="5.4.0" />
+    <PackageReference Include="CycloneDX.Core" Version="5.4.0.1" />^M
+    <PackageReference Include="CycloneDX.Spdx" Version="5.4.0.1" />^M
+    <PackageReference Include="CycloneDX.Utils" Version="5.4.0.1" />^M
+    <PackageReference Include="CycloneDX.Spdx.Interop" Version="5.4.0.1" />^M
     <PackageReference Include="System.CommandLine" Version="2.0.0-beta1.21308.1" />
     <PackageReference Include="System.Security.Cryptography.Xml" Version="6.0.1" />
   </ItemGroup>
````
  * If you tried to build the tool before and it cached the "wrong" library versions
    from the internet instead of ones you tinkered with, `dotnet clean; dotnet build`
    should take care of this.

* VS Code
For debugging to work, ended up copying the DLL and (importantly) matching PDB files
to just be near the binary after every rebuild of the library (added to script above):
````
:; cp -pf ~/cyclonedx-dotnet-library/src/*{Utils,Core,Spdx}*/obj/Debug/net6.0/CycloneDX*.{dll,pdb} \
          ~/cyclonedx-cli/src/cyclonedx/bin/Debug/net6.0/
````
