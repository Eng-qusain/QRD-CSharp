# How to Deploy QRD to GitHub (step by step)

This guide assumes you know Python but are new to C# and GitHub Actions.

---

## Part 1 — Push the code to GitHub

### Step 1: Install Git (if you haven't)
Download from https://git-scm.com/download/win and install with default settings.

### Step 2: Create a new GitHub repository
1. Go to https://github.com/new
2. Name it `QRD` (or anything you like)
3. Set it to **Private** or **Public** — your choice
4. **Do NOT** tick "Add a README" or "Add .gitignore" — the project already has them
5. Click **Create repository**
6. GitHub will show you a URL like `https://github.com/YOUR_NAME/QRD.git` — copy it

### Step 3: Push the project

> **Important**: open a terminal (PowerShell or CMD) **inside the `QRD-CSharp` folder**
> (the one that contains `QRD.csproj` directly). That folder becomes the root of your repo.

```powershell
cd path\to\QRD-CSharp        # ← go INTO this folder first
git init
git add .
git commit -m "Initial commit — C# rebuild of QRD"
git branch -M main
git remote add origin https://github.com/YOUR_NAME/QRD.git
git push -u origin main
```

That's it. Your code is now on GitHub.
After this, GitHub sees `QRD.csproj` at the root of the repo — which is exactly what the CI expects.

---

## Part 2 — GitHub Actions CI/CD (automatic builds)

The file `.github/workflows/ci.yml` is already in the project.
GitHub reads it automatically the moment you push. Nothing else to set up.

### What happens automatically

| When | What GitHub does |
|------|-----------------|
| You push to `main` | Builds the project, runs tests |
| You open a Pull Request | Builds + tests + code quality check |
| You publish a GitHub Release | Builds a `QRD.exe` and attaches it to the release |

### How to create a release and get the installer

1. Go to your repo on GitHub
2. Click **Releases** (right sidebar) → **Create a new release**
3. Click **Choose a tag** → type `v1.0.0` → click **Create new tag**
4. Click **Publish release**
5. GitHub Actions will start building automatically (takes ~5 minutes)
6. When done, a `QRD.exe` appears attached to the release — users can download it directly

### Where to see the build running

Go to your repo → click the **Actions** tab → click the latest workflow run.
You can watch it live. If something fails, the error message tells you exactly what.

---

## Part 3 — Setting up the AI key in GitHub (optional)

If you want CI builds to run with a real API key for integration tests:

1. Go to your repo → **Settings** → **Secrets and variables** → **Actions**
2. Click **New repository secret**
3. Name: `ANTHROPIC_API_KEY`
4. Value: your key from console.anthropic.com
5. Click **Add secret**

The CI workflow does NOT currently use this — but you can add it to ci.yml as:
```yaml
env:
  ANTHROPIC_API_KEY: ${{ secrets.ANTHROPIC_API_KEY }}
```

---

## Part 4 — Working on the code locally (developer workflow)

### One-time setup
```powershell
# 1. Install .NET 8 SDK
#    Download from: https://dotnet.microsoft.com/download/dotnet/8.0
#    Pick: Windows x64 — SDK installer

# 2. Verify install
dotnet --version     # should print 8.x.x

# 3. Clone your repo
git clone https://github.com/YOUR_NAME/QRD.git
cd QRD
```

### Daily workflow
```powershell
# Download packages (only needed once, or when csproj changes)
dotnet restore QRD.csproj

# Run in development (builds and launches the app)
dotnet run

# Build release .exe
dotnet publish QRD.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/
# Your installer is at: publish/QRD.exe
```

### Making a change and pushing it
```powershell
# Edit your files...
git add .
git commit -m "describe what you changed"
git push
# GitHub Actions starts running automatically
```

---

## Part 5 — Recommended free tools for C# development

| Tool | What it is | Download |
|------|-----------|----------|
| **Visual Studio 2022 Community** | Full IDE, free, best for C# | visualstudio.microsoft.com |
| **VS Code + C# Dev Kit** | Lighter editor | code.visualstudio.com |
| **Rider** (JetBrains) | Excellent, 30-day free trial | jetbrains.com/rider |

Visual Studio Community is what most C# developers use. It:
- Highlights errors as you type
- Has one-click Run and Debug buttons
- Manages packages visually (no command line needed)
- Opens `.csproj` files directly — just double-click `QRD.csproj`

