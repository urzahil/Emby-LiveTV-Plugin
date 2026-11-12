define(['baseView', 'loading', 'formHelper', 'emby-input', 'emby-button', 'emby-checkbox'], function (BaseView, loading, formHelper) {
    'use strict';

    var pluginId = 'c5f61da7-48c1-48bd-a2d5-7734a333b6b7';

    function load(view) {
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            view.querySelector('.txtEpgCacheHours').value = config.EpgCacheHours;
            view.querySelector('.txtGuideDays').value = config.GuideDays;
            view.querySelector('.chkEnableCatchupChannel').checked = config.EnableCatchupChannel;
            view.querySelector('.txtCatchupMaxDays').value = config.CatchupMaxDays;
            view.querySelector('.txtHttpTimeout').value = config.HttpTimeoutSeconds;
            loading.hide();
        });
    }

    function save(view) {
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.EpgCacheHours = parseInt(view.querySelector('.txtEpgCacheHours').value, 10) || 6;
            config.GuideDays = parseInt(view.querySelector('.txtGuideDays').value, 10) || 3;
            config.EnableCatchupChannel = view.querySelector('.chkEnableCatchupChannel').checked;
            config.CatchupMaxDays = parseInt(view.querySelector('.txtCatchupMaxDays').value, 10) || 7;
            config.HttpTimeoutSeconds = parseInt(view.querySelector('.txtHttpTimeout').value, 10) || 60;

            ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                loading.hide();
                formHelper.handleConfigurationSavedResponse();
            });
        });
    }

    function View(view, params) {
        BaseView.apply(this, arguments);

        view.querySelector('form').addEventListener('submit', function (e) {
            e.preventDefault();
            e.stopPropagation();
            save(view);
            return false;
        });
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        load(this.view);
    };

    return View;
});
