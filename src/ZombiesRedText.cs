using TMPro;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// A menu text drawn in the zombies mode's red while the mode is on (EFT's own button animations set the colour
    /// every frame, so it's put back after them, in LateUpdate); its own colour again in normal mode.
    /// </summary>
    internal class RedText : MonoBehaviour
    {
        private TMP_Text _text;
        private bool _always;

        public static void On(TMP_Text text, bool always)
        {
            if (text == null)
                return;
            RedText r = text.GetComponent<RedText>() ?? text.gameObject.AddComponent<RedText>();
            r._text = text;
            r._always = always;
        }

        private void LateUpdate()
        {
            if (_text == null || !ZombiesMapSelect.Zombies)
                return;
            Color c = ZombiesMainMenu.Red;
            c.a = _text.color.a;
            _text.color = c;
        }
    }
}
