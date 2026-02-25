MyProjectRoot/
├── .github/
│   └── workflows/
│       ├── build.yml          # CI: Runs on every push
│       └── release.yml        # CD: Runs on version tags (e.g., v1.0.0)
├── src/                       # <--- ALL PRODUCTION CODE GOES HERE
│   ├── MyProject.sln          # Your Visual Studio Solution file
│   ├── MyProject.App/         # Your main application (WPF, Console, etc.)
│   │   ├── MyProject.App.csproj
│   │   └── Program.cs
│   └── MyProject.Core/        # Your logic/library project
│       └── MyProject.Core.csproj
├── tests/                     # <--- ALL TEST CODE GOES HERE
│   └── MyProject.Tests/
│       └── MyProject.Tests.csproj
├── .gitattributes             # Fixes line endings (Reliability)
├── .gitignore                 # Keeps repo clean
├── Directory.Build.props      # Global .NET settings (Reliability)
├── LICENSE                    # MIT License
└── README.md                  # Project documentation

Why this specific layout?
 1. The src/ Folder Requirement: Both the build.yml and release.yml define PROJECT_ROOT: src. When the GitHub Action runs dotnet build src, it looks for a .sln or .csproj inside that folder. If your solution is in the root instead, the build will fail.
 2. Directory.Build.props Placement: By placing this in the root, it automatically applies "Treat Warnings as Errors" and ".NET 8.0" target settings to every project inside src/ and tests/ without you having to edit each file individually.
 3. Visual Studio Compatibility: Windows 11 developers using Visual Studio 2022 expect this layout. It separates the "noise" of build configurations and metadata from your actual C# code.

How to set this up for a new project:
If you are starting your first app in this new template, run these commands from the root of your repository:

# 1. Create the source folder
mkdir src

# 2. Create a new solution inside src
dotnet new sln -n MyNewProject -o src

# 3. Create your main application project
dotnet new console -n MyNewProject.App -o src/MyNewProject.App

# 4. Add the project to the solution
dotnet sln src/MyNewProject.sln add src/MyNewProject.App/MyNewProject.App.csproj

