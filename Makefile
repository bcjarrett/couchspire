.PHONY: help check build format test pr attach-release steam-upload

PROJECT := CouchSpire.csproj

help:
	@echo "Targets:"
	@echo "  check          Build + format-check + Layer A tests (the pre-PR gate)"
	@echo "  build          dotnet build (Release)"
	@echo "  format         dotnet format --verify-no-changes"
	@echo "  test           Layer A: offline Harmony/AccessTools target check"
	@echo "  pr             check, then opens a release PR to master"
	@echo "  attach-release Build the real DLL locally and attach it to the GitHub Release CI created"
	@echo "  steam-upload   Push the current release to the Steam Workshop (TAG=vX.Y.Z)"

# Build + format-check + Layer A tests. Run before opening a release PR — this is the "test" step;
# CI has no game install, so it can only do versioning/tagging, not building or in-game testing.
check: build format test

build:
	dotnet build $(PROJECT) -c Release -nologo -v quiet

format:
	dotnet format $(PROJECT) --verify-no-changes

test:
	dotnet test Tests/CouchSpire.Tests

# Run locally before merging: verifies the build, then opens a PR to master. 
pr: check
	gh pr create --fill --base master

# Run locally after merging a release PR: builds the real DLL and attaches it to the
# GitHub Release that CI already created (CI can't build it itself; see AGENTS.md §7).
attach-release:
	tools/attach-release-asset.sh

# Run locally to push the current release to the Steam Workshop.
#   make steam-upload TAG=v0.2.0
steam-upload:
	tools/upload-steam-workshop.sh $(TAG)
