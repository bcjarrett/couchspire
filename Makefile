.PHONY: check build format pr attach-release steam-upload

PROJECT := CouchSpire.csproj

# Build + format-check. Run before opening a release PR — this is the "test" step; the
# self-hosted CI runner has no game install, so it can only do versioning/tagging, not this.
check: build format

build:
	dotnet build $(PROJECT) -c Release -nologo -v quiet

format:
	dotnet format $(PROJECT) --verify-no-changes

# Run on this Mac before merging: verifies the build, then opens a PR to master. Merging kicks
# off the Release workflow (semantic-release), which tags and cuts the GitHub Release.
pr: check
	gh pr create --fill --base master

# Run on this Mac after merging a release PR: builds the real DLL and attaches it to the
# GitHub Release that CI already created (CI can't build it itself).
attach-release:
	tools/attach-release-asset.sh

# Run on this Mac to push the current release to the Steam Workshop.
#   make steam-upload TAG=v0.2.0
steam-upload:
	tools/upload-steam-workshop.sh $(TAG)
