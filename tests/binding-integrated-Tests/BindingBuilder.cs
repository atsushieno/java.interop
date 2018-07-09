using NUnit.Framework;
using System;
using System.Diagnostics;
using Java.Interop;
using System.IO;
using System.Linq;
using Xamarin.Android.Tools.Bytecode;
using Xamarin.Android.Tools.ApiXmlAdjuster;

namespace BindingIntegrationTests
{
	public class BindingBuilder
	{
		// This is required to get generator.exe deployed next to this assembly
		// so that the default GeneratorPath can probe that it is right there.
		static readonly Type dummy = typeof (MonoDroid.Generation.GenBase);

		[Flags]
		public enum Steps
		{
			Javac = 1,
			Jar = 2,
			ClassParse = 4,
			ApiXmlAdjuster = 8,
			Generator = 16,
			Csc = 32,
			All = Javac | Jar | ClassParse | ApiXmlAdjuster | Generator | Csc
		}

		public const string JavaSourcesSubDir = "java-sources";
		public const string ClassesSubDir = "classes";
		public const string ClassParseSubDir = "class-parse-xml";
		public const string ApiXmlSubDir = "api-xml";
		public const string MetadataXmlSubDir = "metadata";
		public const string CSharpSourcesSubDir = "csharp";

		public static string [] StubPartialSources =>
			Directory.GetFiles (Path.Combine (ThisAssemblyDirectory, "SupportFiles"), "*.cs")
				 .Where (cs => cs.IndexOf ("Java_Lang_", StringComparison.OrdinalIgnoreCase) < 0)
				 .Where (cs => !string.Equals (Path.GetFileName (cs), "JavaObject.cs", StringComparison.OrdinalIgnoreCase))
				 .ToArray ();
		public static string [] StubAllSources =>
			Directory.GetFiles (Path.Combine (ThisAssemblyDirectory, "SupportFiles"), "*.cs");

		static string ThisAssemblyDirectory => Path.GetDirectoryName (new Uri (typeof (BindingBuilder).Assembly.CodeBase).LocalPath);

		public Steps ProcessSteps { get; set; } = Steps.All;

		// entire work (intermediate output) directory
		public string IntermediateOutputPathRelative { get; set; } = "intermediate-output";

		// Used to resolve javac and rt.jar
		public string JdkPath { get; set; }

		public string GeneratorPath { get; set; } = Path.Combine (ThisAssemblyDirectory, "generator.exe");

		public bool UseSystemCsc { get; set; } // we don't have to default to csc-dim, but why "not" ?

		public string CscPath { get; set; }

		static string GetMscorlibPath () => new Uri (typeof (object).Assembly.CodeBase).LocalPath;

		static string ProbeCscDimPath ()
		{
			// For Windows, use nuget package. For Mac/Linux, use SYSMONO dim/csc.exe.
			if (Environment.OSVersion.Platform == PlatformID.Win32NT)
				return Path.Combine ("..", "..", "..", "packages", "xamarin.android.csc.dim.0.1.2", "tools", "csc.exe");
			// assume path relative to framework dll in the GAC.
			return Path.GetFullPath (Path.Combine (GetMscorlibPath (), "..", "dim", "csc.exe"));
		}

		static string GetSystemRuntimeDll () => Path.Combine (Path.GetDirectoryName (GetMscorlibPath ()), "Facades", "System.Runtime.dll");

		static string ProbeJavaHome ()
		{
			var env = Environment.GetEnvironmentVariable ("JAVA_HOME");
			if (!string.IsNullOrEmpty (env))
				return env;
			return "/usr/lib/jvm/java-8-openjdk-amd64/";
		}

		public static BindingBuilder CreateBestBetDefault (BindingProject project)
		{
			return new BindingBuilder (project) {
				JdkPath = ProbeJavaHome (),
				CscPath = ProbeCscDimPath (),
			};
		}

		public BindingBuilder (BindingProject project)
		{
			this.project = project;
		}

		readonly BindingProject project;

		public string IntermediateOutputPathAbsolute => Path.Combine (Path.GetDirectoryName (new Uri (GetType ().Assembly.CodeBase).LocalPath), IntermediateOutputPathRelative, project.Id);

		public void Clean ()
		{
			if (Directory.Exists (IntermediateOutputPathAbsolute))
				Directory.Delete (IntermediateOutputPathAbsolute, true);
		}

		public void Build ()
		{
			Javac ();
			Jar ();
			ClassParse ();
			AdjustApiXml ();
			GenerateBindingSources ();
			CompileBindings ();
		}

		void EnsureDirectory (string dir)
		{
			var parent = Path.GetDirectoryName (dir);
			if (parent != Path.GetPathRoot (parent))
				EnsureDirectory (parent);
			if (!Directory.Exists (dir))
				Directory.CreateDirectory (dir);
		}

		void Javac ()
		{
			if ((ProcessSteps & Steps.Javac) == 0)
				return;

			if (JdkPath == null)
				throw new InvalidOperationException ("JdkPath is not set.");

			var objDir = IntermediateOutputPathAbsolute;
			EnsureDirectory (objDir);

			string sourcesSaved = Path.Combine (objDir, JavaSourcesSubDir);
			EnsureDirectory (sourcesSaved);
			foreach (var item in project.JavaSourceStrings)
				File.WriteAllText (Path.Combine (sourcesSaved, item.FileName), item.Content);
			var sourceFiles = project.JavaSourceFiles.Concat (project.JavaSourceStrings.Select (i => Path.Combine (sourcesSaved, i.FileName)));

			if (project.CompiledClassesDirectory == null)
				project.CompiledClassesDirectory = Path.Combine (objDir, ClassesSubDir);
			EnsureDirectory (project.CompiledClassesDirectory);

			var psi = new ProcessStartInfo () {
				UseShellExecute = false,
				FileName = JdkPath != null ? Path.Combine (JdkPath, "bin", "javac") : "javac",
				Arguments = $"{project.JavacOptions} -d \"{project.CompiledClassesDirectory}\" {string.Join (" ", sourceFiles.Select (s => '"' + s + '"'))}",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			if (project.CustomRuntimeJar != null)
				psi.Arguments += $" -bootclasspath {project.CustomRuntimeJar} -classpath {project.CustomRuntimeJar}";

			project.JavacExecutionOutput = $"Execute javac as: {psi.FileName} {psi.Arguments}\n";

			var proc = new Process () { StartInfo = psi };
			proc.OutputDataReceived += (sender, e) => project.JavacExecutionOutput += e.Data + '\n';
			proc.ErrorDataReceived += (sender, e) => project.JavacExecutionOutput += e.Data + '\n';
			proc.Start ();
			proc.BeginOutputReadLine ();
			proc.BeginErrorReadLine ();
			proc.WaitForExit ();
			if (proc.ExitCode != 0)
				throw new Exception ("Javac failed: " + project.JavacExecutionOutput);
		}

		void Jar ()
		{
			if ((ProcessSteps & Steps.Jar) == 0)
				return;

			if (JdkPath == null)
				throw new InvalidOperationException ("JdkPath is not set.");

			var objDir = IntermediateOutputPathAbsolute;
			if (project.CompiledClassesDirectory == null)
				project.CompiledClassesDirectory = Path.Combine (objDir, ClassesSubDir);
			if (project.CompiledJarFile == null)
				project.CompiledJarFile = Path.Combine (project.CompiledClassesDirectory, project.Id + ".jar");

			var psi = new ProcessStartInfo () {
				UseShellExecute = false,
				FileName = JdkPath != null ? Path.Combine (JdkPath, "bin", "jar") : "jar",
				Arguments = $"cvf \"{project.CompiledJarFile}\" -C \"{project.CompiledClassesDirectory}\" .",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};

			project.JarExecutionOutput = $"Execute jar as: {psi.FileName} {psi.Arguments}\n";

			var proc = new Process () { StartInfo = psi };
			proc.OutputDataReceived += (sender, e) => project.JarExecutionOutput += e.Data + '\n';
			proc.ErrorDataReceived += (sender, e) => project.JarExecutionOutput += e.Data + '\n';
			proc.Start ();
			proc.BeginOutputReadLine ();
			proc.BeginErrorReadLine ();
			proc.WaitForExit ();
			if (proc.ExitCode != 0)
				throw new Exception ("Jar failed: " + project.JarExecutionOutput);
		}

		void ClassParse ()
		{
			if ((ProcessSteps & Steps.ClassParse) == 0)
				return;

			if (project.CompiledJarFile == null && !project.InputJarFiles.Any ())
				throw new InvalidOperationException ("Input Jar files are not set either at CompiledJarFile or InputJarFiles.");

			var objDir = IntermediateOutputPathAbsolute;
			EnsureDirectory (objDir);
			var cpDir = Path.Combine (objDir, ClassParseSubDir);
			EnsureDirectory (cpDir);
			if (project.GeneratedClassParseXmlFile == null)
				project.GeneratedClassParseXmlFile = Path.Combine (cpDir, project.Id + ".class-parse");

			// FIXME: logging
			var cp = new ClassPath ();
			cp.Load (project.CompiledJarFile);
			foreach (var jar in project.InputJarFiles)
				cp.Load (jar);
			cp.SaveXmlDescription (project.GeneratedClassParseXmlFile);
		}

		void AdjustApiXml ()
		{
			if ((ProcessSteps & Steps.ApiXmlAdjuster) == 0)
				return;
				
			var objDir = IntermediateOutputPathAbsolute;
			var cpDir = Path.Combine (objDir, ClassParseSubDir);
			EnsureDirectory (cpDir);
			if (project.GeneratedApiXmlFile == null)
				project.GeneratedApiXmlFile = Path.Combine (objDir, "api.xml");
			if (!File.Exists (project.GeneratedClassParseXmlFile) && !project.ClassParseXmlFiles.Any () && !project.ClassParseXmlStrings.Any ())
				throw new InvalidOperationException ("Input class-parse file does not exist.");

			foreach (var cpSource in project.ClassParseXmlStrings)
				File.WriteAllText (Path.Combine (cpDir, cpSource.FileName), cpSource.Content);
			var cpFiles = project.ClassParseXmlFiles.Concat (project.ClassParseXmlStrings.Select (i => Path.Combine (cpDir, i.FileName)));

			// FIXME: this does not scale for parallel tasking.
			var writer = new StringWriter ();
			Xamarin.Android.Tools.ApiXmlAdjuster.Log.DefaultWriter = writer;

			var api = new JavaApi ();
			if (File.Exists (project.GeneratedClassParseXmlFile))
				api.Load (project.GeneratedClassParseXmlFile);
			foreach (var apixml in cpFiles)
				api.Load (apixml);
			api.Resolve ();
			api.CreateGenericInheritanceMapping ();
			api.MarkOverrides ();
			api.FindDefects ();
			api.Save (project.GeneratedApiXmlFile);
			project.ApiXmlAdjusterExecutionOutput = writer.ToString ();
			if (!project.IgnoreApiXmlAdjusterWarnings && project.ApiXmlAdjusterExecutionOutput != string.Empty)
				throw new Exception ("api-xml-adjuster failed: " + project.ApiXmlAdjusterExecutionOutput);
		}

		void GenerateBindingSources ()
		{
			if ((ProcessSteps & Steps.Generator) == 0)
				return;

			if (GeneratorPath == null)
				throw new InvalidOperationException ("GeneratorPath is not set.");

			var objDir = IntermediateOutputPathAbsolute;
			EnsureDirectory (objDir);

			if (project.GeneratedApiXmlFile == null)
				project.GeneratedApiXmlFile = Path.Combine (objDir, "api.xml");
			if (!File.Exists (project.GeneratedApiXmlFile) && !project.ApiXmlFiles.Any () && !project.ApiXmlStrings.Any ())
				throw new InvalidOperationException ("Input api xml file does not exist.");
			if (project.GeneratedCSharpSourceDirectory == null)
				project.GeneratedCSharpSourceDirectory = Path.Combine (objDir, CSharpSourcesSubDir);

			string apiXmlSaved = Path.Combine (objDir, ApiXmlSubDir);
			EnsureDirectory (apiXmlSaved);
			foreach (var item in project.ApiXmlStrings)
				File.WriteAllText (Path.Combine (apiXmlSaved, item.FileName), item.Content);
			var apiXmlFiles = project.ApiXmlFiles.Concat (project.ApiXmlStrings.Select (i => Path.Combine (apiXmlSaved, i.FileName)));

			string metadataSaved = Path.Combine (objDir, MetadataXmlSubDir);
			EnsureDirectory (metadataSaved);
			foreach (var item in project.MetadataXmlStrings)
				File.WriteAllText (Path.Combine (metadataSaved, item.FileName), item.Content);
			var metadataFiles = project.MetadataXmlFiles.Concat (project.MetadataXmlStrings.Select (i => Path.Combine (metadataSaved, i.FileName)));

			var psi = new ProcessStartInfo () {
				UseShellExecute = false,
				FileName = GeneratorPath,
				Arguments = $"{project.GeneratorOptions}" +
					$" {(File.Exists (project.GeneratedApiXmlFile) ? project.GeneratedApiXmlFile : string.Empty)}" +
					$" {string.Join (" ", apiXmlFiles.Select (s => '"' + s + '"'))}" +
					$" {string.Join (" ", metadataFiles.Select (s => " --fixup=\"" + s + '"'))}" +
					$" {string.Join (" ", project.ReferenceDlls.Select (s => " -r \"" + s + '"'))}" +
					$" --csdir=\"{project.GeneratedCSharpSourceDirectory}\"",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};

			project.GeneratorExecutionOutput = $"Execute generator as: {psi.FileName} {psi.Arguments}\n";

			var proc = new Process () { StartInfo = psi };
			proc.OutputDataReceived += (sender, e) => project.GeneratorExecutionOutput += e.Data + '\n';
			proc.ErrorDataReceived += (sender, e) => project.GeneratorExecutionOutput += e.Data + '\n';
			proc.Start ();
			proc.BeginOutputReadLine ();
			proc.BeginErrorReadLine ();
			proc.WaitForExit ();
			if (proc.ExitCode != 0)
				throw new Exception ("generator failed: " + project.GeneratorExecutionOutput);
		}

		void CompileBindings ()
		{
			if ((ProcessSteps & Steps.Csc) == 0)
				return;

			if (CscPath == null)
				throw new InvalidOperationException ("CscPath is not set.");

			var objDir = IntermediateOutputPathAbsolute;
			EnsureDirectory (objDir);

			if (project.GeneratedCSharpSourceFiles == null)
				project.GeneratedCSharpSourceDirectory = Path.Combine (objDir, CSharpSourcesSubDir);
			if (!Directory.GetFiles (project.GeneratedCSharpSourceDirectory, "*.cs").Any () && !project.CSharpSourceFiles.Any () && !project.CSharpSourceStrings.Any ())
				throw new InvalidOperationException ("No C# sources exist.");
			if (project.GeneratedDllFile == null)
				project.GeneratedDllFile = Path.Combine (objDir, project.Id + ".dll");

			foreach (var item in project.CSharpSourceStrings)
				File.WriteAllText (Path.Combine (project.GeneratedCSharpSourceDirectory, item.FileName), item.Content);
			var csFiles = project.CSharpSourceFiles.AsEnumerable ();

			switch (project.CSharpStubUsage) {
			case CSharpStubUsage.Partial:
				csFiles = csFiles.Concat (StubPartialSources);
				break;
			case CSharpStubUsage.Full:
				csFiles = csFiles.Concat (StubAllSources);
				break;
			}

			string localSystemRuntime = Path.Combine (ThisAssemblyDirectory, "System.Runtime.dll");
			if (!File.Exists (localSystemRuntime))
				File.Copy (GetSystemRuntimeDll (), localSystemRuntime);

			var psi = new ProcessStartInfo () {
				UseShellExecute = false,
				FileName = UseSystemCsc ? "csc" : CscPath,
				Arguments = $" -t:library -unsafe" + 
					$" -out:\"{project.GeneratedDllFile}\" {project.GeneratedCSharpSourceDirectory}{Path.DirectorySeparatorChar}*.cs " +
					$" {string.Join (" ", csFiles.Select (s => '"' + s + '"'))}" +
					$" -r:{ThisAssemblyDirectory}{Path.DirectorySeparatorChar}System.Runtime.dll" +
					$" -r:{ThisAssemblyDirectory}{Path.DirectorySeparatorChar}Java.Interop.dll" +
					$" {string.Join (" ", project.ReferenceDlls.Select (s => " -r \"" + s + '"'))}",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};

			project.CscExecutionOutput = $"Execute csc as: {psi.FileName} {psi.Arguments}\n";

			var proc = new Process () { StartInfo = psi };
			proc.OutputDataReceived += (sender, e) => project.CscExecutionOutput += e.Data + '\n';
			proc.ErrorDataReceived += (sender, e) => project.CscExecutionOutput += e.Data + '\n';
			proc.Start ();
			proc.BeginOutputReadLine ();
			proc.BeginErrorReadLine ();
			proc.WaitForExit ();
			if (proc.ExitCode != 0)
				throw new Exception ("csc failed: " + project.CscExecutionOutput);
		}
	}
}

