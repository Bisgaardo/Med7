using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.SceneManagement;

public class ConsentScreenManager : MonoBehaviour
{
    [Header("UI References")]
    public GameObject consentPanel;
    public TMP_InputField ageInput;
    public Button agreeButton;
    public TextMeshProUGUI warningText;

    [Header("Scene Settings")]
    [Tooltip("Leave empty if test happens in the same scene.")]
    public string nextSceneName = "";

    private void Start()
    {
        warningText.text = "";
        agreeButton.onClick.AddListener(OnAgreeClicked);
    }

    private void OnAgreeClicked()
    {
        string ageText = ageInput.text.Trim();

        // Reset warnings
        warningText.text = "";

        // Validate inputs
        if (string.IsNullOrEmpty(ageText) )
        {
            warningText.text = "Please fill out both fields before continuing.";
            return;
        }

        if (!int.TryParse(ageText, out int age))
        {
            warningText.text = "Please enter a valid number for age.";
            return;
        }

        // Save participant info
        PlayerPrefs.SetInt("UserAge", age);

        // If nextSceneName is defined, load it
        if (!string.IsNullOrEmpty(nextSceneName))
        {
            SceneManager.LoadScene(nextSceneName);
        }
        else
        {
            // Otherwise, just hide the consent panel
            consentPanel.SetActive(false);
        }
    }
}
