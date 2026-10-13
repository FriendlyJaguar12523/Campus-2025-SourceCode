// ModSettingsViewModel.h
#pragma once
#include "../../Utils/globals.h"
#include "../../RRUI/RRUIBaseLocalViewModel.h"  // Adjust path as needed

namespace RecRoom::AppUI {
    class ModSettingsViewModel : public RRUIBaseLocalViewModel {
    public:
        CLASS("RecRoom.AppUI.Runtime", "RecRoom.AppUI", "ModSettingsViewModel");

        union {
            MEMBER(bool, modEnabled, 96);                     // backing field
            MEMBER(MVVMDatabinding::DataItem<bool>*, ModEnabledDataItem, 104);
            MEMBER(MVVMDatabinding::DataItemAction*, SaveDataItem, 112);
            MEMBER(System::Collections::Generic::List<MVVMDatabinding::IDataItem*>*, generatedDataItemList, 120);
        };

        // Constructor
        System::Void.ctor() {
            METHOD(System::Void, (ModSettingsViewModel*, MethodInfo*), "System.Void .ctor()");
            return function(this, nullptr);
        }

        // Property getters/setters
        bool get_ModEnabled() {
            METHOD(bool, (ModSettingsViewModel*, MethodInfo*), "System.Boolean get_ModEnabled()");
            return function(this, nullptr);
        }
        void set_ModEnabled(bool value) {
            METHOD(void, (ModSettingsViewModel*, bool, MethodInfo*), "System.Void set_ModEnabled(System.Boolean)");
            return function(this, value, nullptr);
        }

        // Action: Save settings
        void Save() {
            METHOD(void, (ModSettingsViewModel*, MethodInfo*), "System.Void Save()");
            return function(this, nullptr);
        }

        // Overrides
        virtual System::Collections::Generic::List<MVVMDatabinding::IDataItem*>* get_DataItemList() override {
            METHOD(System::Collections::Generic::List<MVVMDatabinding::IDataItem*>*, (ModSettingsViewModel*, MethodInfo*), "System.Collections.Generic.List<MVVMDatabinding.IDataItem> get_DataItemList()");
            return function(this, nullptr);
        }
        virtual bool get_UseReflection() override {
            METHOD(bool, (ModSettingsViewModel*, MethodInfo*), "System.Boolean get_UseReflection()");
            return function(this, nullptr);
        }
        virtual void InitializeDataItems() override {
            METHOD(void, (ModSettingsViewModel*, MethodInfo*), "System.Void InitializeDataItems()");
            return function(this, nullptr);
        }
        virtual void Awake() override {
            METHOD(void, (ModSettingsViewModel*, MethodInfo*), "System.Void Awake()");
            return function(this, nullptr);
        }
        virtual void OnDestroy() override {
            METHOD(void, (ModSettingsViewModel*, MethodInfo*), "System.Void OnDestroy()");
            return function(this, nullptr);
        }

        // Private methods for data binding
        void GatherDataItems() {
            METHOD(void, (ModSettingsViewModel*, MethodInfo*), "System.Void GatherDataItems()");
            return function(this, nullptr);
        }
    };
}