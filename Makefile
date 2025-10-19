.PHONY: test build

test:
	dotnet test

build: test
	dotnet publish src/FSDBSlim.Api/FSDBSlim.Api.csproj -c Release -r linux-arm64 \
		/p:PublishSingleFile=true /p:SelfContained=true /p:PublishTrimmed=true /p:TrimMode=partial
	dotnet publish src/FSDBSlim.Api/FSDBSlim.Api.csproj -c Release -r linux-arm \
		/p:PublishSingleFile=true /p:SelfContained=true /p:PublishTrimmed=true /p:TrimMode=partial
