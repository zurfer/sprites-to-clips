using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Text;
using Object = UnityEngine.Object;

namespace Cayaware.Editor.Tools
{
    /// <summary>
    ///     Converts animation sprite sheets to animation clips
    /// </summary>
    public class SpritesheetsToAnimationClips : EditorWindow
    {
        #region Types
        private readonly struct SourceTexture : IEquatable<SourceTexture>
        {
            public readonly string Folder;
            public readonly Texture Texture;

            public SourceTexture(string folder, Texture2D texture)
            {
                Folder = folder;
                Texture = texture;
            }

            public override string ToString()
            {
                return Path.Combine(Folder, Texture.name);
            }

            public override int GetHashCode() => (Folder, Texture).GetHashCode();

            public bool Equals(SourceTexture other)
            {
                return Folder == other.Folder && Equals(Texture, other.Texture);
            }

            public override bool Equals(object obj)
            {
                return obj is SourceTexture other && Equals(other);
            }
        }

        private readonly struct SourceSprite : IEquatable<SourceSprite>
        {
            public readonly SourceTexture Parent;
            public readonly Sprite Sprite;
            public readonly string Name;
            public readonly int Frame;

            public SourceSprite(SourceTexture parent, Sprite sprite, string name, int frame)
            {
                Parent = parent;
                Sprite = sprite;
                Name = name;
                Frame = frame;
            }

            public override int GetHashCode() => Name.GetHashCode();

            public bool Equals(SourceSprite other)
            {
                return Parent.Equals(other.Parent) && Equals(Sprite, other.Sprite) && Name == other.Name && Frame == other.Frame;
            }

            public override bool Equals(object obj)
            {
                return obj is SourceSprite other && Equals(other);
            }
        }
        
        private class UserPrefs
        {
            public string InpFolder = Path.Combine("Assets","Textures");
            public string OutFolder = Path.Combine("Assets", "Animation");
            public FramePrefixChar InpFramePrefix = FramePrefixChar.Underscore;
            public int OutFrameRate = 12;
            public OutputFormat OutFormat = OutputFormat.Clip;
            public OutputNaming OutNaming = OutputNaming.KeepOriginal;
            public override string ToString()
            {
                return $"Input folder: {InpFolder}, Output folder: {OutFolder}, Input frame separator: {InpFramePrefix}, " +
                       $"Output frame rate: {OutFrameRate}, Output format: {OutFormat}, Output naming: {OutNaming}";
            }
        }

        private enum InputSource
        {
            StartUp,
            Selection,
            Folder,
        }

        public enum FramePrefixChar
        {
            Underscore,
            Dot,
        }

        public enum OutputFormat
        {
            [InspectorName("<clip name>")]
            Clip,
            [InspectorName("<texture name>_<clip name>")]
            TextureClip,
            [InspectorName("<folder name>_<texture name>_<clip name>")]
            FolderTextureClip,
        }

        public enum OutputNaming
        {
            [InspectorName("Keep original names")]
            KeepOriginal,
            [InspectorName("lower_case")]
            SnakeCase,
            [InspectorName("PascalCase")]
            PascalCase,
            [InspectorName("Pascal_Case")]
            PascalCaseUnderscores,
            [InspectorName("Capitalize_first_char")]
            CapitalizeFirstChar,
        }
        #endregion

        #region Private members
        private const string WindowTitle = "Sprites to Animation Clips";
        private const string MenuItemPath = "Window/Cayaware/" + WindowTitle;
        private const string EditorPrefsKey = "Cayaware.SpritesToClips";
        private const int FileDialogButtonWidth = 40;
        private const int VertPaddingDivisor = 4;

        private readonly UserPrefs m_userPrefs = new ();

        private InputSource m_inputSource = InputSource.StartUp;
        private readonly List<SourceTexture> m_sourceTextures = new();
        private Vector2 m_scrollViewPos = Vector2.zero;
        private readonly Dictionary<int, List<SourceSprite>> m_hashmap = new();
        #endregion

        [MenuItem(MenuItemPath)]
        private static void Init()
        {
            var window = GetWindow<SpritesheetsToAnimationClips>(false, WindowTitle, true);
            window.Show();
        }

        private void OnEnable()
        {
            LoadEditorPrefs(EditorPrefsKey, m_userPrefs);
            LoadFromUserSelection(m_sourceTextures);
            m_inputSource = m_sourceTextures.Count > 0 ? InputSource.Selection : InputSource.StartUp;
            BuildHashMap();
        }

        private void OnSelectionChange()
        {
            LoadFromUserSelection(m_sourceTextures);
            m_inputSource = InputSource.Selection;
            BuildHashMap();
            Repaint();
        }

        private void OnGUI()
        {
            var hasChanged = false;
            
            // Input 
            GUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField("Input", EditorStyles.boldLabel);
            // Select input folder
            {
                GUILayout.BeginHorizontal();
                switch (m_inputSource) {
                    case InputSource.StartUp:
                        GUI.enabled = false;
                        _ = EditorGUILayout.TextField($"Folder", m_userPrefs.InpFolder);
                        GUI.enabled = true;
                        if (GUILayout.Button("Load", GUILayout.Width(FileDialogButtonWidth))) {
                            LoadAllFromFolder(m_userPrefs.InpFolder, m_sourceTextures);
                            m_inputSource = InputSource.Folder;
                            hasChanged = true;
                        }
                        break;
                    case InputSource.Selection:
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.PrefixLabel("Selection");
                        EditorGUILayout.LabelField(string.Join(" ", m_sourceTextures.Select(p => p.Texture.name)), EditorStyles.textField);
                        GUILayout.EndHorizontal();
                        break;
                    case InputSource.Folder:
                        m_userPrefs.InpFolder = EditorGUILayout.TextField($"Folder", m_userPrefs.InpFolder);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
                if (GUILayout.Button(EditorGUIUtility.IconContent("Folder", "Select input folder"), GUILayout.Width(FileDialogButtonWidth))) {
                    var folder = EditorUtility.OpenFolderPanel("Select input folder", m_userPrefs.InpFolder, string.Empty);
                    if (!string.IsNullOrEmpty(folder)) {
                        folder = FileUtil.GetProjectRelativePath(folder);
                        LoadAllFromFolder(folder, m_sourceTextures);
                        m_userPrefs.InpFolder = folder;
                        m_inputSource = InputSource.Folder;
                        hasChanged = true;
                    }
                }
                GUILayout.EndHorizontal();
            }
            // Input frame prefix
            {
                EditorGUI.BeginChangeCheck();
                m_userPrefs.InpFramePrefix = (FramePrefixChar)EditorGUILayout.EnumPopup("Frame Prefix", m_userPrefs.InpFramePrefix);
                if(EditorGUI.EndChangeCheck()) hasChanged = true;
            }
            GUILayout.EndVertical();
            // End of input

            GUILayout.Space(EditorGUIUtility.singleLineHeight / VertPaddingDivisor);

            // Output
            GUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            // Select output folder 
            {
                GUILayout.BeginHorizontal();
                m_userPrefs.OutFolder = EditorGUILayout.TextField("Folder", m_userPrefs.OutFolder);
                if (GUILayout.Button(EditorGUIUtility.IconContent("Folder", "Select output folder"), GUILayout.Width(FileDialogButtonWidth))) {
                    var folder = EditorUtility.SaveFolderPanel("Select output folder", m_userPrefs.OutFolder, string.Empty);
                    if (!string.IsNullOrEmpty(folder)) {
                        folder = FileUtil.GetProjectRelativePath(folder);
                        m_userPrefs.OutFolder = folder;
                        hasChanged = true;
                    }
                }
                GUILayout.EndHorizontal();
            }
            // Output naming
            {
                EditorGUI.BeginChangeCheck();
                m_userPrefs.OutFormat = (OutputFormat)EditorGUILayout.EnumPopup("Format", m_userPrefs.OutFormat);
                m_userPrefs.OutNaming = (OutputNaming)EditorGUILayout.EnumPopup("Naming", m_userPrefs.OutNaming);
                m_userPrefs.OutFrameRate = EditorGUILayout.IntField("Frame Rate", m_userPrefs.OutFrameRate);
                if (EditorGUI.EndChangeCheck()) hasChanged = true;
            }
            EditorGUILayout.EndVertical();
            // End of output

            GUILayout.Space(EditorGUIUtility.singleLineHeight / VertPaddingDivisor);

            // Preview
            m_scrollViewPos = GUILayout.BeginScrollView(m_scrollViewPos);
            GUILayout.BeginVertical("Preview", GUI.skin.window);
            foreach (var item in m_hashmap) {
                EditorGUILayout.LabelField($"{item.Value[0].Parent} -> {MakeClipName(item.Value[0])}: {item.Value.Count}");
            }
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            // End of preview

            GUILayout.Space(EditorGUIUtility.singleLineHeight / VertPaddingDivisor);

            if (hasChanged) {
                SaveEditorPrefs(EditorPrefsKey, m_userPrefs);
                BuildHashMap();
            }

            var numClips = m_hashmap.Count;
            GUI.enabled = numClips > 0;
            if (GUILayout.Button($"Create {numClips} animation clips in {m_userPrefs.OutFolder}")) {
                WriteOutClips();
            }
            GUI.enabled = true;
            
        }

        private void WriteOutClips()
        {
            int numProcessed = 1, totalCount = m_hashmap.Count;

            foreach (var item in m_hashmap) {
                CreateAnimationClip(item.Value, m_userPrefs.OutFolder);
                EditorUtility.DisplayProgressBar("Progress", $"Exporting {numProcessed} out of {totalCount}...", (float)numProcessed / totalCount);
                numProcessed++;
            }
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
        }

        private static void SaveEditorPrefs(string key, UserPrefs prefs)
        {
            var json = string.Empty;
            try {
                json = JsonUtility.ToJson(prefs);
            }
            catch (Exception e) {
                Debug.Log(e.ToString());
            }
            
            if(string.IsNullOrEmpty(json)) return;
            EditorPrefs.SetString(key, json);
        }

        private static void LoadEditorPrefs(string key, UserPrefs prefs)
        {
            var json = EditorPrefs.GetString(key);
            if (string.IsNullOrEmpty(json)) return;
            try {
                JsonUtility.FromJsonOverwrite(json, prefs);
            }
            catch (Exception e) {
                Debug.Log(e);
            }
        }

        private static string GetSubFolderNameFromPath(string path) => 
            Path.GetDirectoryName(path)
                ?.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .LastOrDefault();


        private static void LoadAllFromFolder(string folderPath, ICollection<SourceTexture> assets)
        {
            assets.Clear();
            foreach (var guid in AssetDatabase.FindAssets("t: texture", new[] { folderPath })) {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                var folderName = GetSubFolderNameFromPath(assetPath);
                if (!asset || string.IsNullOrEmpty(folderName)) continue;
                assets.Add(new SourceTexture(folderName, asset));
            }
        }

        private static void LoadFromUserSelection(ICollection<SourceTexture> assets)
        {
            assets.Clear();
            var guids = Selection.assetGUIDs;
            if (guids.Length <= 0) return;
            foreach (var guid in guids) {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var folderName = GetSubFolderNameFromPath(path);
                var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (!asset || string.IsNullOrEmpty(folderName)) continue;
                assets.Add(new SourceTexture(folderName, asset));
            }
        }


        private void BuildHashMap()
        {
            m_hashmap.Clear();
            foreach (var sourceTexture in m_sourceTextures) {
                foreach (var sourceSprite in GetSpritesFromTexture(sourceTexture)) {
                    var key = sourceSprite.GetHashCode();
                    if (m_hashmap.TryGetValue(key, out var value))
                        value.Add(sourceSprite);
                    else {
                        m_hashmap[key] = new List<SourceSprite> { sourceSprite };
                    }
                }
            }
        }

        private IEnumerable<SourceSprite> GetSpritesFromTexture(SourceTexture source)
        {
            var sprites = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(source.Texture)).OfType<Sprite>();
            foreach (var sprite in sprites) {
                if (ParseSpriteName(sprite, out var spriteName, out var frame)) {
                    yield return new SourceSprite(source, sprite, spriteName, frame);
                }
            }
        }

        private string MakeClipName(SourceSprite sourceSprite, char separator = '_')
        {
            var fullname = m_userPrefs.OutFormat switch
            {
                OutputFormat.Clip => sourceSprite.Name,
                OutputFormat.TextureClip => $"{sourceSprite.Parent.Texture.name}{separator}{sourceSprite.Name}",
                OutputFormat.FolderTextureClip => $"{sourceSprite.Parent.Folder}{separator}{sourceSprite.Parent.Texture.name}{separator}{sourceSprite.Name}",
                _ => throw new ArgumentOutOfRangeException()
            };

            if (m_userPrefs.OutNaming == OutputNaming.KeepOriginal) return fullname;

            var split = fullname.Split('.', '_', ' ', '-');
            for (var i = 0; i < split.Length; i++) split[i] = RemovePunctuations(split[i]);

            if (split.Length == 0) return string.Empty;

            switch (m_userPrefs.OutNaming) {
                case OutputNaming.SnakeCase:
                    for (var i = 0; i < split.Length; i++) {
                        split[i] = split[i].ToLower();
                    }

                    return string.Join(separator, split);

                case OutputNaming.PascalCase:
                    for (var i = 0; i < split.Length; i++) {
                        split[i] = FirstCharToUpper(split[i]);
                    }
                
                    return string.Concat(split);

                case OutputNaming.PascalCaseUnderscores:
                    for (var i = 0; i < split.Length; i++) {
                        split[i] = FirstCharToUpper(split[i]);
                    }

                    return string.Join(separator, split);

                case OutputNaming.CapitalizeFirstChar:
                    for (var i = 0; i < split.Length; i++) {
                        split[i] = split[i].ToLower();
                    }
                    split[0] = FirstCharToUpper(split[0]);

                    return string.Join(separator, split);

                case OutputNaming.KeepOriginal:
                default:
                    return string.Empty;
            }
        }

        private static string RemovePunctuations(string input)
        {
            StringBuilder sb = new ();
            foreach (var c in input.Where(c => !char.IsPunctuation(c))) {
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string FirstCharToUpper(string input)
        {
            if(string.IsNullOrEmpty(input)) return input;

            return input.Length == 1 ? input.ToUpper() : string.Concat(input[0].ToString().ToUpper(), input[1..]);
        }  

        private bool ParseSpriteName(Object sprite, out string spriteName, out int frame)
        {
            var separator = m_userPrefs.InpFramePrefix switch
            {
                FramePrefixChar.Underscore => '_',
                FramePrefixChar.Dot => '.',
                _ => throw new ArgumentOutOfRangeException()
            };
            
            var lastSeparatorPos = sprite.name.LastIndexOf(separator);
            if (lastSeparatorPos == -1) {
                Debug.Log($"Failed to parse sprite {sprite.name}: no '{separator}' separators have been found");
                spriteName = string.Empty;
                frame = -1;
                return false;
            }

            spriteName = sprite.name[..lastSeparatorPos];
                
            if (string.IsNullOrEmpty(spriteName)) {
                Debug.Log($"Failed to parse sprite {sprite.name}: can't extract the clip name");
                frame = -1;
                return false;
            }

            if (int.TryParse(sprite.name[(lastSeparatorPos + 1)..], out frame)) return true;
            Debug.Log($"Failed to parse sprite {sprite.name}; the frame number does not exist");
            return false;
        }

        private void CreateAnimationClip(IReadOnlyList<SourceSprite> spr, string animationPath)
        {
            var sprites = spr.ToList();
            sprites.Sort((a, b) => a.Frame < b.Frame ? -1 : 1);
            
            var animationClipName = MakeClipName(sprites[0]);

            var clip = new AnimationClip
            {
                frameRate = m_userPrefs.OutFrameRate,
                wrapMode = WrapMode.Loop,
            };
            

            // Create binding curve
            var curveBinding = new EditorCurveBinding
            {
                path = string.Empty,
                propertyName = "m_Sprite",
                type = typeof(SpriteRenderer)
            };

            // Create keyframes
            var spriteKeyFrames = new ObjectReferenceKeyframe[sprites.Count];
            for (var i = 0; i <= sprites.Count; i++) {
                var index = Mathf.Clamp(i, 0, sprites.Count - 1);

                spriteKeyFrames[index] = new ObjectReferenceKeyframe
                {
                    value = sprites[index].Sprite,
                    time = ((float)index)/m_userPrefs.OutFrameRate,
                };
            }

            AnimationUtility.SetObjectReferenceCurve(clip, curveBinding, spriteKeyFrames);
            AssetDatabase.CreateAsset(clip, Path.Combine(animationPath, animationClipName + ".anim"));
            AssetDatabase.SaveAssets();
        }
    }
}
