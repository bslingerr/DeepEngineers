using System;
using UnityEngine.SceneManagement;

namespace DeepEngineers.SN
{
    /// <summary>
 /// `snPhase` detection (CONTRACT §5.4 C0: 0 None, 1 Booting, 2 Menu,... 7 Quitting). S0 only reaches Menu.
 /// <para>Menu = the main-menu controller exists: <c>uGUI_MainMenu.main != null</c>
 /// The XMenu scene load is also recorded from
 /// <c>SceneManager.sceneLoaded</c> for the log. The check runs every frame while Booting (one managed null test).</para>
 /// </summary>
    internal sealed class MenuDetector
    {
        public const string MenuSceneName = "XMenu";

        private bool _attached;

        public bool XMenuLoaded { get; private set; }

        public void Attach()
        {
            if (_attached) return;
            SceneManager.sceneLoaded += OnSceneLoaded;
            _attached = true;
        }

        public void Detach()
        {
            if (!_attached) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _attached = false;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                string name = scene.name;
                if (name == MenuSceneName) XMenuLoaded = true;
                DeLog.Info("DE.Scene loaded name=" + name + " mode=" + mode);
            }
            catch (Exception)
            {
                // a scene callback must never throw into Unity
            }
        }

        /// <summary>True when the main menu is up.</summary>
        public bool MenuReady()
        {
            return uGUI_MainMenu.main != null;
        }

        public string Describe()
        {
            return "uGUI_MainMenu.main present, XMenu loaded=" + (XMenuLoaded ? "true" : "false");
        }
    }
}
