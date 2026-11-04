srpm:
	dnf -y install dotnet-sdk-10.0 git python3 rpm-build
	git submodule update --init --recursive
	./packaging/create-srpm.sh "$(outdir)"
