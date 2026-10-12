import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('audit', Path(__file__).with_name('check-vulnerabilities.py'))
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class AuditTests(unittest.TestCase):
    def clean(self):
        return {'version': 1, 'sources': ['https://api.nuget.org/v3/index.json'], 'projects': [{'path': 'test.csproj', 'frameworks': [{'framework': 'net10.0', 'topLevelPackages': []}]}]}

    def test_clean_result(self):
        audit.validate(self.clean())

    def test_incomplete_response(self):
        for report in ({}, {'version': 1}, {'version': 1, 'projects': []}):
            with self.assertRaises(ValueError):
                audit.validate(report)

    def test_feed_problem(self):
        report = self.clean(); report['problems'] = ['unreachable']
        with self.assertRaises(ValueError):
            audit.validate(report)

    def test_direct_and_transitive_findings(self):
        for group in ('topLevelPackages', 'transitivePackages'):
            report = self.clean()
            report['projects'][0]['frameworks'][0][group] = [{'id': 'bad', 'vulnerabilities': [{'severity': 'Low'}]}]
            with self.assertRaises(ValueError):
                audit.validate(report)

    def test_missing_frameworks(self):
        report = self.clean(); report['projects'][0].pop('frameworks')
        with self.assertRaises(ValueError):
            audit.validate(report)

    def test_nested_feed_problem(self):
        report = self.clean(); report['projects'][0]['frameworks'][0]['problems'] = ['failure']
        with self.assertRaises(ValueError):
            audit.validate(report)

    def test_unexpected_feed(self):
        report = self.clean(); report['sources'] = ['https://untrusted.example/']
        with self.assertRaises(ValueError):
            audit.validate(report)

    def test_missing_package_arrays(self):
        report = self.clean(); report['projects'][0]['frameworks'][0].pop('topLevelPackages')
        with self.assertRaises(ValueError):
            audit.validate(report)


if __name__ == '__main__':
    unittest.main()
