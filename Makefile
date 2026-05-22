.PHONY: all clean NixDevShellName

PROJECT = ./Ipk2/Ipk2/Ipk2.csproj
TESTS = ./Ipk2/Ipk2.Tests/
EXECUTABLE = ipk-rdt

all:
	dotnet publish $(PROJECT) -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o .
	chmod +x ./$(EXECUTABLE)

test:
	dotnet test $(TESTS)

clean:
	rm -rf ./bin ./obj ./$(EXECUTABLE) ipk-rdt ipk-rdt.pdb

NixDevShellName:
	@echo "csharp"
