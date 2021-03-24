import PropTypes from 'prop-types';
import React from 'react';
import { inputTypes } from 'Helpers/Props';
import FieldSet from 'Components/FieldSet';
import FormGroup from 'Components/Form/FormGroup';
import FormLabel from 'Components/Form/FormLabel';
import FormInputGroup from 'Components/Form/FormInputGroup';

function ExternalServicesSettings(props) {
  const {
    advancedSettings,
    settings,
    onInputChange
  } = props;

  const {
    tmdbApiKey,
    fanartApiKey
  } = settings;

  return (
    <FieldSet legend="External Services">
      <FormGroup
        advancedSettings={advancedSettings}
        isAdvanced={false}
      >
        <FormLabel>TMDB API Key (Required)</FormLabel>

        <FormInputGroup
          type={inputTypes.TEXT}
          name="tmdbApiKey"
          helpText="The Movie Database (TMDb) API Key to obtain Series data and commonly used images"
          helpLink="https://www.themoviedb.org/documentation/api"
          helpTextWarning="Requires restart to take effect"
          onChange={onInputChange}
          {...tmdbApiKey}
        />
      </FormGroup>

      <FormGroup
        advancedSettings={advancedSettings}
        isAdvanced={true}
      >
        <FormLabel>Fanart API Key (Optional)</FormLabel>

        <FormInputGroup
          type={inputTypes.TEXT}
          name="fanartApiKey"
          helpText="Fanart API Key to obtain extra images"
          helpLink="https://fanart.tv/2015/01/personal-api-keys/"
          helpTextWarning="Requires restart to take effect"
          onChange={onInputChange}
          {...fanartApiKey}
        />
      </FormGroup>
    </FieldSet>
  );
}

ExternalServicesSettings.propTypes = {
  advancedSettings: PropTypes.bool.isRequired,
  settings: PropTypes.object.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default ExternalServicesSettings;
