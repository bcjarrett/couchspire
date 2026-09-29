.PHONY: help check build format test pr attach-release steam-upload snapshot

PROJECT := CouchSpire.csproj
# Steam game branches this repo builds for (Sts2Paths.props, AGENTS.md §5).
GAME_TARGETS := main beta
TARGET ?= main

help:
	@echo "Targets:"
	@echo "  check          Build + format-check + Layer A tests, for both game branches (the pre-PR gate)"
	@echo "  build          dotnet build (Release), main and beta game branches"
	@echo "  format         dotnet format --verify-no-changes, main and beta"
	@echo "  test           Layer A: offline Harmony/AccessTools target check, main and beta"
	@echo "  pr             check, then opens a release PR to master"
	@echo "  attach-release Build the real DLL locally and attach it to the GitHub Release CI created"
	@echo "  steam-upload   Push a release to a Steam Workshop item (TAG=vX.Y.Z TARGET=main|beta)"
	@echo "  snapshot       Save the installed game's assemblies for building (TARGET=main|beta; Steam on that branch)"

# Build + format-check + Layer A tests. Run before opening a release PR — this is the "test" step;
# CI has no game install, so it can only do versioning/tagging, not building or in-game testing.
check: build format test

build:
	@set -e; for t in $(GAME_TARGETS); do echo "==> build ($$t)"; dotnet build $(PROJECT) -c Release -p:GameTarget=$$t -nologo -v quiet; done

format:
	@set -e; for t in $(GAME_TARGETS); do echo "==> format ($$t)"; GameTarget=$$t dotnet format $(PROJECT) --verify-no-changes; done

test:
	@set -e; for t in $(GAME_TARGETS); do echo "==> Layer A ($$t)"; dotnet test Tests/CouchSpire.Tests -p:GameTarget=$$t; done

# Run locally before merging: verifies the build, then opens a PR to master. 
pr: check
	gh pr create --fill --base master

# Run locally after merging a release PR: builds the real DLL and attaches it to the
# GitHub Release that CI already created (CI can't build it itself; see AGENTS.md §7).
attach-release:
	tools/attach-release-asset.sh

# Run locally to push a release to the Steam Workshop, once per game branch's item.
#   make steam-upload TAG=v0.2.0 TARGET=main
#   make steam-upload TAG=v0.2.0 TARGET=beta
steam-upload:
	tools/upload-steam-workshop.sh --target $(TARGET) $(TAG)

# Run with Steam on that game branch, and again after it gets a game patch.
#   make snapshot TARGET=beta
snapshot:
	tools/snapshot-game-ref.sh $(TARGET)
