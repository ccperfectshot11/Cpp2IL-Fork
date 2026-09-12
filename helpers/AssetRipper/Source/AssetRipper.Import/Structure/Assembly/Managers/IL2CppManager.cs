using AsmResolver.DotNet;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Platforms;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.ProcessingLayers;
using LibCpp2IL;
using Cpp2IlApi = Cpp2IL.Core.Cpp2IlApi;

namespace AssetRipper.Import.Structure.Assembly.Managers;

public sealed class IL2CppManager : BaseManager
{
	static IL2CppManager()
	{
		InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_32);
		InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_64);
		InstructionSetRegistry.RegisterInstructionSet<WasmInstructionSet>(DefaultInstructionSets.WASM);
		InstructionSetRegistry.RegisterInstructionSet<ArmV7InstructionSet>(DefaultInstructionSets.ARM_V7);
		bool useNewArm64 = false;
		if (useNewArm64)
		{
			InstructionSetRegistry.RegisterInstructionSet<NewArmV8InstructionSet>(DefaultInstructionSets.ARM_V8);
		}
		else
		{
			InstructionSetRegistry.RegisterInstructionSet<Arm64InstructionSet>(DefaultInstructionSets.ARM_V8);
		}

		LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport();
	}

	public static List<Cpp2IlProcessingLayer> DefaultProcessingLayers { get; } =
	[
		new AttributeAnalysisProcessingLayer(),
		new MethodOverrideNameFixer(),
	];

	public static AsmResolverDllOutputFormatDefault DefaultOutputFormat { get; } = new();

	/// <summary>
	/// Straturile folosite la ScriptContentLevel.Level3. Erau null, iar cand sunt null Level3 cade inapoi
	/// pe DefaultProcessingLayers - adica exact ce face Level2. Masurat pe acest joc: cele doua niveluri
	/// produceau proiecte identice, 2.671 de scripturi cu 74,6% din corpuri scrise `return default(...)`.
	/// </summary>
	public static List<Cpp2IlProcessingLayer>? RecoveryProcessingLayers { get; set; } =
	[
		new AttributeInjectorProcessingLayer(),
		new StableRenamingProcessingLayer(),
		new AttributeAnalysisProcessingLayer(),
		// Fara el, fiecare camp si metoda privata citita din alt tip da CS0122 la compilarea C#-ului
		// exportat: codul recuperat pastreaza accesele pe care il2cpp le-a inline-at peste granita de
		// accesibilitate, dar C#-ul nu are voie sa le scrie. Masurat: 8.140 de erori de felul asta,
		// din care 1.916 numai pe EntityRef.Raw.
		new PublicizerProcessingLayer(),
		new MethodOverrideNameFixer(),
	];

	/// <summary>
	/// Formatul care chiar scrie IL-ul recuperat in corpuri. Fara el, Level3 nu inseamna nimic.
	/// </summary>
	public static AsmResolverDllOutputFormat? RecoveryOutputFormat { get; set; } = new AsmResolverDllOutputFormatIlRecovery();

	public static event Action? ClearStaticState;

	public string? GameAssemblyPath { get; private set; }
	public string? UnityPlayerPath { get; private set; }
	public string? GameDataPath { get; private set; }
	public string? MetaDataPath { get; private set; }
	public UnityVersion UnityVersion { get; private set; }
	/// <summary>
	/// For when analysis is reimplimented in Cpp2IL.
	/// </summary>
	private readonly ScriptContentLevel contentLevel;

	public IL2CppManager(Action<string> requestAssemblyCallback, ScriptContentLevel level) : base(requestAssemblyCallback)
	{
		contentLevel = level;
	}

	public override ScriptingBackend ScriptingBackend => ScriptingBackend.IL2Cpp;

	public override void Initialize(PlatformGameStructure gameStructure)
	{
		string? gameDataPath = gameStructure.GameDataPath;
		if (string.IsNullOrWhiteSpace(gameDataPath))
		{
			throw new ArgumentException($"{nameof(gameStructure.GameDataPath)} cannot be null or whitespace.", nameof(gameStructure));
		}

		GameDataPath = gameDataPath;
		GameAssemblyPath = gameStructure.Il2CppGameAssemblyPath;
		UnityPlayerPath = gameStructure.UnityPlayerPath;
		MetaDataPath = gameStructure.Il2CppMetaDataPath;

		UnityVersion = gameStructure.Version ?? Cpp2IlApi.DetermineUnityVersion(UnityPlayerPath, GameDataPath);

		if (UnityVersion == default)
		{
			throw new Exception("Could not determine the unity version");
		}
		else
		{
			Logger.Info(LogCategory.Import, $"During Il2Cpp initialization, found Unity version: {UnityVersion}");
		}

		Logger.SendStatusChange("loading_step_parse_il2cpp_metadata");

		ClearStaticState?.Invoke();

		Cpp2IlApi.InitializeLibCpp2Il(GameAssemblyPath!, MetaDataPath!, UnityVersion, false);

		Logger.SendStatusChange("loading_step_generate_dummy_dll");

		List<Cpp2IlProcessingLayer> processingLayers = contentLevel == ScriptContentLevel.Level3
			? RecoveryProcessingLayers ?? DefaultProcessingLayers
			: DefaultProcessingLayers;

		foreach (Cpp2IlProcessingLayer cpp2IlProcessingLayer in processingLayers)
		{
			cpp2IlProcessingLayer.PreProcess(Cpp2IlApi.CurrentAppContext, processingLayers);
		}

		foreach (Cpp2IlProcessingLayer cpp2IlProcessingLayer in processingLayers)
		{
			cpp2IlProcessingLayer.Process(Cpp2IlApi.CurrentAppContext);
		}

		AsmResolverDllOutputFormat outputFormat = contentLevel == ScriptContentLevel.Level3
			? RecoveryOutputFormat ?? DefaultOutputFormat
			: DefaultOutputFormat;

		List<AssemblyDefinition> assemblies = outputFormat.BuildAssemblies(Cpp2IlApi.CurrentAppContext);

		foreach (AssemblyDefinition assembly in assemblies)
		{
			Add(assembly);
		}
	}

	~IL2CppManager()
	{
		Dispose(false);
	}
}
