#!/usr/bin/env python3
"""Fail closed on NuGet advisory results, including transitives (list itself exits 0 on findings)."""
import json
import subprocess
import sys


def validate(report):
    if (report.get('version') != 1 or not isinstance(report.get('projects'), list)
            or len(report['projects']) != 1 or report.get('sources') != ['https://api.nuget.org/v3/index.json']):
        raise ValueError('Incomplete NuGet advisory response')
    if report.get('problems'):
        raise ValueError('NuGet advisory query reported problems')
    for project in report['projects']:
        if not project.get('path') or not project.get('frameworks') or project.get('problems'):
            raise ValueError('Incomplete project advisory response')
        for framework in project['frameworks']:
            if (framework.get('framework') != 'net10.0' or framework.get('problems')
                    or not isinstance(framework.get('topLevelPackages'), list)):
                raise ValueError('Incomplete framework advisory response')
            for group in ('topLevelPackages', 'transitivePackages'):
                packages = framework.get(group, [])
                if not isinstance(packages, list):
                    raise ValueError('Malformed advisory packages')
                for package in packages:
                    if package.get('vulnerabilities'):
                        raise ValueError('Vulnerable NuGet dependency detected')


def main():
    for project in ('SelectAlgorithm.csproj', 'tests/SelectAlgorithm.Tests.csproj'):
        command = ['dotnet', 'list', project, 'package', '--vulnerable', '--include-transitive',
                   '--source', 'https://api.nuget.org/v3/index.json', '--format', 'json']
        result = subprocess.run(command, capture_output=True, text=True, check=True, timeout=180)
        validate(json.loads(result.stdout))
        print(f'PASS current-feed NuGet direct + transitive audit: {project}')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, TypeError, KeyError, AttributeError, OSError, subprocess.SubprocessError):
        print('FAIL NuGet advisory gate: vulnerability, incomplete result, or feed/tool failure', file=sys.stderr)
        sys.exit(1)
