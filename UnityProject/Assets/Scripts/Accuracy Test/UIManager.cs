using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class UIManager : MonoBehaviour
{
   [SerializeField] TMP_InputField ageInput;
    [SerializeField] TMP_Dropdown deviceDropdown;
    [SerializeField] TMP_InputField averageComputerTime;
    [SerializeField] TMP_InputField UUIDInput;

    [SerializeField] Button startButton; 

    [SerializeField] GameObject consentMenu; 
    [SerializeField] GameObject background; 
    [SerializeField] GameObject endScreen; 

    private string immutableUUID;

    void Start()
    {
        
    }

    public void handleFinished(string uuid)
    {
        immutableUUID = uuid;
        UUIDInput.text = uuid;
        endScreen.SetActive(true);
        background.SetActive(true);
        
    }

    public void handleStart()
    {
        int age = int.Parse(ageInput.text);
        int computerTime = int.Parse(averageComputerTime.text);
        string device = deviceDropdown.options[deviceDropdown.value].text;
        

        ManagerScript manager = FindFirstObjectByType<ManagerScript>();
        manager.age = age;
        manager.device = device;
        manager.computerTime = computerTime;

        consentMenu.SetActive(false);
        background.SetActive(false);

    }

    bool isValidInput(string s)
    {
        return int.TryParse(s, out _);
    }

    void Update()
    {


        if(string.IsNullOrWhiteSpace(ageInput.text) || string.IsNullOrWhiteSpace(averageComputerTime.text) || !isValidInput(ageInput.text) || !isValidInput(averageComputerTime.text))
        {
            startButton.interactable = false;
        }
        else
        {
            startButton.interactable = true;
        }
        
        UUIDInput.text = immutableUUID;
    }
}
