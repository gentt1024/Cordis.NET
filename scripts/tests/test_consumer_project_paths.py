import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET


SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))


def load_consumer(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), SCRIPTS / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class StopBeforeBuild(Exception):
    pass


class ConsumerProjectPathsTests(unittest.TestCase):
    def test_remote_project_preserves_xml_metacharacters_in_its_private_cache_path(self):
        consumer = load_consumer("verify-typert")
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            source = directory / "remote & candidate"
            source.mkdir()
            runtime = directory / "runtime"
            runtime.mkdir()
            (runtime / "client.mjs").write_text("", encoding="utf-8")
            observed = []

            def before_build(command, **kwargs):
                project = Path(command[2])
                observed.append(ET.parse(project).findtext(".//RestorePackagesPath"))
                raise StopBeforeBuild

            with patch.object(sys, "argv", ["verify-typert.py", "--packages", str(directory / "packages"),
                    "--runtime", str(runtime), "--output", str(directory / "output")]), \
                 patch.object(consumer.tempfile, "mkdtemp", return_value=str(source)), \
                 patch.object(consumer.subprocess, "run", side_effect=before_build):
                with self.assertRaises(StopBeforeBuild):
                    consumer.main()
            self.assertEqual(observed, [str(source / "cache")])

    def test_multi_entry_projects_preserve_cache_and_dependency_paths_with_xml_metacharacters(self):
        consumer = load_consumer("verify-clr-multi-entry")
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            source = directory / "multi-entry & candidate"
            source.mkdir()
            native = directory / "libm.so.6"
            native.write_bytes(b"fixture native input; no build or native invocation is performed")
            observed = []
            dependencies = {}

            def before_build(command, **kwargs):
                for path in sorted(source.rglob("*.csproj")):
                    project = ET.parse(path)
                    observed.append(project.findtext(".//RestorePackagesPath"))
                    references = project.findall(".//ProjectReference")
                    if references:
                        dependencies[path.stem] = [reference.attrib["Include"] for reference in references]
                raise StopBeforeBuild

            with patch.object(sys, "argv", ["verify-clr-multi-entry.py", "--packages", str(directory / "packages"),
                    "--output", str(directory / "output"), "--multi-assembly-only"]), \
                 patch.object(consumer.tempfile, "mkdtemp", return_value=str(source)), \
                 patch.object(consumer.sys, "platform", "linux"), \
                 patch.object(consumer.glob, "glob", return_value=[str(native)]), \
                 patch.object(consumer.subprocess, "run", side_effect=before_build):
                with self.assertRaises(StopBeforeBuild):
                    consumer.main()
            self.assertEqual(observed, [str(source / "cache")] * 12)
            private_first = str(source / "PrivateFirst/PrivateFirst.csproj")
            self.assertEqual(dependencies, {
                "Bridge": [private_first], "PrivateA": [private_first], "FirstB": [private_first],
                "SecondB": [str(source / "PrivateSecond/PrivateSecond.csproj")],
                "DelayedA": [str(source / "Bridge/Bridge.csproj")],
                "BridgeB": [str(source / "Bridge/Bridge.csproj")],
            })


if __name__ == "__main__":
    unittest.main()
