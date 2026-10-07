"""Formatting checks isolate current source bytes and constrain every tool stage."""
import contextlib
import importlib.util
import io
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / "format.py"


class FormattingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="cordis-format-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        subprocess.run(["git", "init", "-q", str(self.root)], check=True)
        self.write(".gitignore", "obj/\n")
        self.write("Tracked.cs", "class Before { }\n")
        self.write("Deleted.cs", "class Deleted { }\n")
        self.write(".editorconfig", "root = true\n")
        subprocess.run(["git", "add", "."], cwd=self.root, check=True)
        self.write("Tracked.cs", "class Current { }\n")
        (self.root / "Deleted.cs").unlink()
        self.write("new/Added.cs", "class Added { }\n")
        self.write("new/.editorconfig", "[*.cs]\nindent_size = 4\n")
        self.write("global.json", '{"sdk":{"version":"10.0.111"}}\n')
        self.write("dotnet-tools.json", '{"version":1,"isRoot":true,"tools":{}}\n')
        self.write("obj/Ignored.cs", "class Ignored { }\n")
        self.expected = {"Tracked.cs", "new/Added.cs"}
        spec = importlib.util.spec_from_file_location("cordis_format", SCRIPT)
        self.formatter = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.formatter)
        self.formatter.ROOT = self.root

    def write(self, name, text):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def check(self, dotnet):
        real_run = subprocess.run

        def run(command, **kwargs):
            if command[0] == "dotnet":
                return dotnet(list(command), Path(kwargs["cwd"]))
            return real_run(command, **kwargs)

        output = io.StringIO()
        with patch("sys.argv", [str(SCRIPT), "--check"]), \
                patch("subprocess.run", side_effect=run), \
                contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
            result = self.formatter.main()
        return result, output.getvalue()

    def test_check_preserves_concurrent_edits_even_when_tools_fail(self):
        for tool_failure in (False, True):
            with self.subTest(tool_failure=tool_failure):
                self.write("Tracked.cs", "class Current { }\n")
                initial = {p: p.read_bytes() for p in self.root.rglob("*") if p.is_file()}

                def dotnet(command, cwd):
                    if command[1:3] == ["tool", "restore"]:
                        (cwd / "restore-output").write_text("temporary tool output")
                        return subprocess.CompletedProcess(command, 0)
                    if "cleanupcode" in command:
                        (cwd / "Tracked.cs").write_text("class Formatted { }\n")
                        self.write("Tracked.cs", "class ConcurrentEdit { }\n")
                        self.write("DuringCheck.cs", "class NewEdit { }\n")
                        if tool_failure:
                            raise subprocess.CalledProcessError(2, command)
                    return subprocess.CompletedProcess(command, 0)

                if tool_failure:
                    with self.assertRaises(subprocess.CalledProcessError):
                        self.check(dotnet)
                else:
                    result, _ = self.check(dotnet)
                    self.assertEqual(1, result)
                self.assertEqual("class ConcurrentEdit { }\n", (self.root / "Tracked.cs").read_text())
                self.assertEqual("class NewEdit { }\n", (self.root / "DuringCheck.cs").read_text())
                for path, content in initial.items():
                    if path.name != "Tracked.cs":
                        self.assertEqual(content, path.read_bytes(), str(path))
                self.assertFalse((self.root / "restore-output").exists())

    def test_all_stages_use_current_snapshot_and_same_source_set(self):
        stages = []
        original = {p: p.read_bytes() for p in self.root.rglob("*") if p.is_file()}

        def dotnet(command, cwd):
            self.assertNotEqual(self.root, cwd)
            self.assertEqual("class Current { }\n", (cwd / "Tracked.cs").read_text())
            self.assertFalse((cwd / "Deleted.cs").exists())
            self.assertFalse((cwd / "obj/Ignored.cs").exists())
            for name in (".editorconfig", "new/.editorconfig", "global.json", "dotnet-tools.json"):
                self.assertEqual((self.root / name).read_bytes(), (cwd / name).read_bytes())
            if command[1] == "restore":
                (cwd / "Generated.cs").write_text("class Generated { }\n")
            if "cleanupcode" in command or command[1] == "format":
                if "cleanupcode" in command:
                    selected = {arg for arg in command if arg.endswith(".cs")}
                else:
                    selected = set(command[command.index("--include") + 1:]) if "--include" in command else {
                        p.relative_to(cwd).as_posix() for p in cwd.rglob("*.cs")}
                self.assertEqual(self.expected, selected)
                stages.append(command)
            return subprocess.CompletedProcess(command, 0)

        result, _ = self.check(dotnet)
        self.assertEqual(0, result)
        self.assertEqual(4, len(stages))
        self.assertEqual(original, {p: p.read_bytes() for p in self.root.rglob("*") if p.is_file()})

    def test_later_formatter_disagreement_fails_without_touching_source(self):
        def dotnet(command, cwd):
            if command[1] == "format":
                (cwd / "Tracked.cs").write_text("class DifferentLayout { }\n")
            return subprocess.CompletedProcess(command, 0)

        result, output = self.check(dotnet)
        self.assertEqual(1, result)
        self.assertIn("FAIL dotnet format whitespace", output)
        self.assertIn("Tracked.cs", output)
        self.assertEqual("class Current { }\n", (self.root / "Tracked.cs").read_text())


if __name__ == "__main__":
    unittest.main()
